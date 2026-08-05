//! Windows 系统控制：Service 权限模式、开机启动与防火墙。

#![cfg(windows)]

use std::{
    ffi::OsStr,
    io::{Read, Write},
    net::{SocketAddr, TcpStream},
    os::windows::{ffi::OsStrExt, process::CommandExt as _},
    path::{Path, PathBuf},
    process::Command,
    ptr::{null, null_mut},
    time::{Duration, Instant},
};

use thiserror::Error;
use windows_sys::Win32::{
    Foundation::{CloseHandle, WAIT_FAILED, WAIT_OBJECT_0},
    System::Threading::{GetExitCodeProcess, WaitForSingleObject, INFINITE},
    UI::{
        Shell::{ShellExecuteExW, ShellExecuteW, SEE_MASK_NOCLOSEPROCESS, SHELLEXECUTEINFOW},
        WindowsAndMessaging::{SW_HIDE, SW_SHOWNORMAL},
    },
};

const CREATE_NO_WINDOW: u32 = 0x0800_0000;
const DETACHED_PROCESS: u32 = 0x0000_0008;
const SERVICE_ADDRESS: &str = "127.0.0.1:35179";
const WEB_GATEWAY_ADDRESS: &str = "127.0.0.1:35180";
const TASK_NAME: &str = "XhMonitor";
const FIREWALL_RULE_NAME: &str = "XhMonitor Web Access";

#[derive(Debug, Error)]
pub enum SystemControlError {
    #[error("无法定位当前 Desktop 可执行文件")]
    DesktopExecutableNotFound,
    #[error("无法定位 xhm-service.exe")]
    ServiceExecutableNotFound,
    #[error("系统命令 {command} 启动失败: {source}")]
    CommandStart {
        command: String,
        #[source]
        source: std::io::Error,
    },
    #[error("系统命令 {command} 执行失败，退出码 {code}: {message}")]
    CommandFailed {
        command: String,
        code: u32,
        message: String,
    },
    #[error("UAC 提权启动失败: {0}")]
    Elevation(std::io::Error),
    #[error("UAC 提权进程等待失败: {0}")]
    ElevatedProcessWait(std::io::Error),
    #[error("UAC 提权进程退出码读取失败: {0}")]
    ElevatedProcessExitCode(std::io::Error),
    #[error("{endpoint} 未在限定时间内{state}")]
    EndpointTransitionTimeout {
        endpoint: &'static str,
        state: &'static str,
    },
    #[error("管理员模式标记写入失败: {0}")]
    AdminFlag(std::io::Error),
}

fn wide(value: &OsStr) -> Vec<u16> {
    value.encode_wide().chain(Some(0)).collect()
}

fn optional_wide(value: Option<&OsStr>) -> (Vec<u16>, *const u16) {
    match value {
        Some(value) => {
            let encoded = wide(value);
            let pointer = encoded.as_ptr();
            (encoded, pointer)
        }
        None => (Vec::new(), null()),
    }
}

fn run_elevated_and_wait(
    executable: &OsStr,
    arguments: &OsStr,
    working_directory: Option<&OsStr>,
) -> Result<u32, SystemControlError> {
    let verb = wide(OsStr::new("runas"));
    let executable = wide(executable);
    let arguments = wide(arguments);
    let (_directory_storage, directory) = optional_wide(working_directory);
    let mut info: SHELLEXECUTEINFOW = unsafe { std::mem::zeroed() };
    info.cbSize = std::mem::size_of::<SHELLEXECUTEINFOW>() as u32;
    info.fMask = SEE_MASK_NOCLOSEPROCESS;
    info.hwnd = null_mut();
    info.lpVerb = verb.as_ptr();
    info.lpFile = executable.as_ptr();
    info.lpParameters = arguments.as_ptr();
    info.lpDirectory = directory;
    info.nShow = SW_HIDE;

    let launched = unsafe { ShellExecuteExW(&mut info) };
    if launched == 0 || info.hProcess.is_null() {
        return Err(SystemControlError::Elevation(
            std::io::Error::last_os_error(),
        ));
    }

    let wait_result = unsafe { WaitForSingleObject(info.hProcess, INFINITE) };
    if wait_result == WAIT_FAILED {
        let error = std::io::Error::last_os_error();
        unsafe { CloseHandle(info.hProcess) };
        return Err(SystemControlError::ElevatedProcessWait(error));
    }
    if wait_result != WAIT_OBJECT_0 {
        unsafe { CloseHandle(info.hProcess) };
        return Err(SystemControlError::ElevatedProcessWait(
            std::io::Error::other(format!("unexpected wait result {wait_result}")),
        ));
    }

    let mut exit_code = 0;
    let exit_code_read = unsafe { GetExitCodeProcess(info.hProcess, &mut exit_code) };
    let exit_code_error = std::io::Error::last_os_error();
    unsafe { CloseHandle(info.hProcess) };
    if exit_code_read == 0 {
        return Err(SystemControlError::ElevatedProcessExitCode(exit_code_error));
    }
    Ok(exit_code)
}

fn launch_elevated(
    executable: &Path,
    working_directory: Option<&Path>,
) -> Result<(), SystemControlError> {
    let verb = wide(OsStr::new("runas"));
    let executable = wide(executable.as_os_str());
    let (_directory_storage, directory) = optional_wide(working_directory.map(Path::as_os_str));
    let result = unsafe {
        ShellExecuteW(
            null_mut(),
            verb.as_ptr(),
            executable.as_ptr(),
            null(),
            directory,
            SW_SHOWNORMAL,
        )
    };
    if result as usize <= 32 {
        return Err(SystemControlError::Elevation(
            std::io::Error::from_raw_os_error(result as i32),
        ));
    }
    Ok(())
}

fn run_hidden(command: &str, arguments: &[&str]) -> Result<String, SystemControlError> {
    let output = Command::new(command)
        .args(arguments)
        .creation_flags(CREATE_NO_WINDOW)
        .output()
        .map_err(|source| SystemControlError::CommandStart {
            command: command.to_string(),
            source,
        })?;
    if output.status.success() {
        return Ok(String::from_utf8_lossy(&output.stdout).trim().to_string());
    }
    Err(SystemControlError::CommandFailed {
        command: command.to_string(),
        code: output.status.code().unwrap_or(-1) as u32,
        message: String::from_utf8_lossy(&output.stderr).trim().to_string(),
    })
}

fn admin_mode_flag_path() -> Result<PathBuf, SystemControlError> {
    let executable =
        std::env::current_exe().map_err(|_| SystemControlError::DesktopExecutableNotFound)?;
    Ok(executable
        .parent()
        .unwrap_or_else(|| Path::new("."))
        .join("admin-mode.flag"))
}

pub fn is_admin_mode_enabled() -> bool {
    admin_mode_flag_path().is_ok_and(|path| path.exists())
}

fn write_admin_mode_flag(enabled: bool) -> Result<(), SystemControlError> {
    let path = admin_mode_flag_path()?;
    if enabled {
        std::fs::write(path, "1").map_err(SystemControlError::AdminFlag)
    } else {
        match std::fs::remove_file(path) {
            Ok(()) => Ok(()),
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => Ok(()),
            Err(error) => Err(SystemControlError::AdminFlag(error)),
        }
    }
}

fn service_executable_from(desktop_executable: &Path) -> Option<PathBuf> {
    let desktop_directory = desktop_executable.parent()?;
    let release_root = desktop_directory.parent()?;
    let candidates = [
        desktop_directory.join("xhm-service.exe"),
        release_root.join("Service/xhm-service.exe"),
        release_root.join("xhm-service.exe"),
    ];
    candidates.into_iter().find(|path| path.is_file())
}

fn service_executable() -> Result<PathBuf, SystemControlError> {
    let desktop =
        std::env::current_exe().map_err(|_| SystemControlError::DesktopExecutableNotFound)?;
    service_executable_from(&desktop).ok_or(SystemControlError::ServiceExecutableNotFound)
}

fn is_endpoint_listening(address: &'static str) -> bool {
    let address: SocketAddr = address.parse().expect("static address must be valid");
    TcpStream::connect_timeout(&address, Duration::from_millis(150)).is_ok()
}

fn is_service_listening() -> bool {
    is_endpoint_listening(SERVICE_ADDRESS)
}

fn wait_for_endpoint(
    address: &'static str,
    endpoint: &'static str,
    listening: bool,
    timeout: Duration,
) -> Result<(), SystemControlError> {
    let deadline = Instant::now() + timeout;
    while Instant::now() < deadline {
        if is_endpoint_listening(address) == listening {
            return Ok(());
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    Err(SystemControlError::EndpointTransitionTimeout {
        endpoint,
        state: if listening { "启动" } else { "停止" },
    })
}

fn wait_for_service(listening: bool, timeout: Duration) -> Result<(), SystemControlError> {
    wait_for_endpoint(SERVICE_ADDRESS, "Service", listening, timeout)
}

fn stop_service_elevated() -> Result<(), SystemControlError> {
    run_elevated_and_wait(
        OsStr::new("cmd.exe"),
        OsStr::new(
            "/d /s /c \"taskkill /f /t /im xhm-service.exe >nul 2>&1 & taskkill /f /t /im lhm-bridge.exe >nul 2>&1\"",
        ),
        None,
    )?;
    wait_for_service(false, Duration::from_secs(5))
}

fn stop_service_for_restart(service_is_elevated: bool) -> Result<(), SystemControlError> {
    if !service_is_elevated {
        for image_name in ["xhm-service.exe", "lhm-bridge.exe"] {
            let _ = run_hidden("taskkill.exe", &["/f", "/t", "/im", image_name]);
        }
        if wait_for_service(false, Duration::from_secs(2)).is_ok() {
            return Ok(());
        }
    }
    stop_service_elevated()
}

fn start_service(elevated: bool) -> Result<(), SystemControlError> {
    let executable = service_executable()?;
    let directory = executable.parent().map(Path::to_path_buf);
    if elevated {
        launch_elevated(&executable, directory.as_deref())?;
    } else {
        Command::new(&executable)
            .current_dir(directory.as_deref().unwrap_or_else(|| Path::new(".")))
            .creation_flags(DETACHED_PROCESS)
            .spawn()
            .map_err(|source| SystemControlError::CommandStart {
                command: executable.display().to_string(),
                source,
            })?;
    }
    wait_for_service(true, Duration::from_secs(10))
}

pub fn ensure_service_running() -> Result<bool, SystemControlError> {
    if is_service_listening() {
        return Ok(false);
    }
    start_service(is_admin_mode_enabled())?;
    Ok(true)
}

pub fn restart_service_for_settings() -> Result<(), SystemControlError> {
    let elevated = is_admin_mode_enabled();
    stop_service_for_restart(elevated).and_then(|()| start_service(elevated))
}

fn is_web_gateway_ready() -> bool {
    let address: SocketAddr = WEB_GATEWAY_ADDRESS
        .parse()
        .expect("static web gateway address must be valid");
    let Ok(mut stream) = TcpStream::connect_timeout(&address, Duration::from_millis(250)) else {
        return false;
    };
    let timeout = Some(Duration::from_millis(500));
    if stream.set_read_timeout(timeout).is_err() || stream.set_write_timeout(timeout).is_err() {
        return false;
    }
    if stream
        .write_all(
            b"GET /api/v1/config/health HTTP/1.1\r\nHost: 127.0.0.1:35180\r\nConnection: close\r\n\r\n",
        )
        .is_err()
    {
        return false;
    }
    let mut response = [0_u8; 2048];
    let Ok(bytes_read) = stream.read(&mut response) else {
        return false;
    };
    response[..bytes_read]
        .windows(b"Healthy".len())
        .any(|window| window == b"Healthy")
}

pub fn wait_for_web_gateway() -> Result<(), SystemControlError> {
    let deadline = Instant::now() + Duration::from_secs(10);
    while Instant::now() < deadline {
        if is_web_gateway_ready() {
            return Ok(());
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    Err(SystemControlError::EndpointTransitionTimeout {
        endpoint: "Web gateway",
        state: "启动",
    })
}

pub fn apply_admin_mode(enabled: bool) -> Result<(), SystemControlError> {
    let previous = is_admin_mode_enabled();
    if previous == enabled {
        return Ok(());
    }

    write_admin_mode_flag(enabled)?;
    if let Err(error) = stop_service_for_restart(previous).and_then(|()| start_service(enabled)) {
        let _ = write_admin_mode_flag(previous);
        return Err(error);
    }
    if is_startup_enabled() {
        set_startup(true)?;
    }
    Ok(())
}

pub fn is_startup_enabled() -> bool {
    run_hidden("schtasks.exe", &["/query", "/tn", TASK_NAME]).is_ok()
}

pub fn set_startup(enabled: bool) -> Result<(), SystemControlError> {
    if !enabled {
        return match run_hidden("schtasks.exe", &["/delete", "/tn", TASK_NAME, "/f"]) {
            Ok(_) => Ok(()),
            Err(_) if !is_startup_enabled() => Ok(()),
            Err(error) => Err(error),
        };
    }

    let executable =
        std::env::current_exe().map_err(|_| SystemControlError::DesktopExecutableNotFound)?;
    let task_command = format!("\"{}\"", executable.display());
    let run_level = if is_admin_mode_enabled() {
        "HIGHEST"
    } else {
        "LIMITED"
    };
    let arguments = format!(
        "/create /tn \"{TASK_NAME}\" /tr \"{task_command}\" /sc onlogon /rl {run_level} /f"
    );

    let exit_code = if run_level == "HIGHEST" {
        run_elevated_and_wait(OsStr::new("schtasks.exe"), OsStr::new(&arguments), None)?
    } else {
        let output = Command::new("schtasks.exe")
            .args([
                "/create",
                "/tn",
                TASK_NAME,
                "/tr",
                &task_command,
                "/sc",
                "onlogon",
                "/rl",
                run_level,
                "/f",
            ])
            .creation_flags(CREATE_NO_WINDOW)
            .output()
            .map_err(|source| SystemControlError::CommandStart {
                command: "schtasks.exe".to_string(),
                source,
            })?;
        if output.status.success() {
            0
        } else {
            return Err(SystemControlError::CommandFailed {
                command: "schtasks.exe".to_string(),
                code: output.status.code().unwrap_or(-1) as u32,
                message: String::from_utf8_lossy(&output.stderr).trim().to_string(),
            });
        }
    };

    if exit_code == 0 {
        Ok(())
    } else {
        Err(SystemControlError::CommandFailed {
            command: "schtasks.exe".to_string(),
            code: exit_code,
            message: "创建开机启动任务失败".to_string(),
        })
    }
}

pub fn is_firewall_rule_exists() -> bool {
    run_hidden(
        "netsh.exe",
        &[
            "advfirewall",
            "firewall",
            "show",
            "rule",
            &format!("name={FIREWALL_RULE_NAME}"),
        ],
    )
    .is_ok_and(|output| output.contains(FIREWALL_RULE_NAME))
}

pub fn configure_firewall(enabled: bool, port: u16) -> Result<String, SystemControlError> {
    if !enabled && !is_firewall_rule_exists() {
        return Ok("无需配置防火墙".to_string());
    }

    let rule_exists = is_firewall_rule_exists();
    let arguments = if enabled && rule_exists {
        format!(
            "advfirewall firewall set rule name=\"{FIREWALL_RULE_NAME}\" new dir=in action=allow protocol=TCP localport={port} profile=private,domain enable=yes"
        )
    } else if enabled {
        format!(
            "advfirewall firewall add rule name=\"{FIREWALL_RULE_NAME}\" dir=in action=allow protocol=TCP localport={port} profile=private,domain enable=yes"
        )
    } else {
        format!("advfirewall firewall delete rule name=\"{FIREWALL_RULE_NAME}\"")
    };
    let exit_code = run_elevated_and_wait(OsStr::new("netsh.exe"), OsStr::new(&arguments), None)?;
    if exit_code != 0 {
        return Err(SystemControlError::CommandFailed {
            command: "netsh.exe".to_string(),
            code: exit_code,
            message: "防火墙规则更新失败".to_string(),
        });
    }

    Ok(if enabled {
        format!("防火墙规则已启用（端口 {port}）")
    } else {
        "防火墙规则已删除".to_string()
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn service_path_resolves_dev_layout() {
        let temp =
            std::env::temp_dir().join(format!("xhm-system-controls-dev-{}", std::process::id()));
        let service = temp.join("xhm-service.exe");
        std::fs::create_dir_all(&temp).unwrap();
        std::fs::write(&service, b"").unwrap();

        assert_eq!(
            service_executable_from(&temp.join("xhm-desktop.exe")),
            Some(service.clone())
        );

        std::fs::remove_dir_all(temp).unwrap();
    }

    #[test]
    fn service_path_resolves_release_layout() {
        let temp = std::env::temp_dir().join(format!(
            "xhm-system-controls-release-{}",
            std::process::id()
        ));
        let desktop_dir = temp.join("Desktop");
        let service_dir = temp.join("Service");
        let service = service_dir.join("xhm-service.exe");
        std::fs::create_dir_all(&desktop_dir).unwrap();
        std::fs::create_dir_all(&service_dir).unwrap();
        std::fs::write(&service, b"").unwrap();

        assert_eq!(
            service_executable_from(&desktop_dir.join("xhm-desktop.exe")),
            Some(service.clone())
        );

        std::fs::remove_dir_all(temp).unwrap();
    }
}
