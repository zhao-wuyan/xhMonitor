//! 复用官方签名 RyzenSMU 模块；不安装驱动、不创建子进程、不回退到旧驱动。

use std::sync::Mutex;

use super::{lock_ryzen, ryzen_error, PowerScheme, Result, RyzenAdjBackend, RyzenAdjSnapshot};

#[derive(Default)]
struct BackendState {
    session: Option<Session>,
    disabled: bool,
}

pub(super) struct PawnIoBackend {
    state: Mutex<BackendState>,
}

impl PawnIoBackend {
    pub(super) fn new() -> Self {
        Self {
            state: Mutex::new(BackendState::default()),
        }
    }

    fn with_session<T>(&self, operation: impl FnOnce(&mut Session) -> Result<T>) -> Result<T> {
        let mut state = lock_ryzen(&self.state, "PawnIO session")?;
        if state.disabled {
            return Err(ryzen_error(
                "PawnIO power backend disabled until service restart",
            ));
        }
        if state.session.is_none() {
            match Session::open() {
                Ok(session) => state.session = Some(session),
                Err(error) => {
                    state.disabled = true;
                    return Err(error);
                }
            }
        }
        operation(state.session.as_mut().expect("PawnIO session initialized"))
    }
}

impl RyzenAdjBackend for PawnIoBackend {
    fn is_available(&self) -> bool {
        cfg!(windows)
            && !self
                .state
                .lock()
                .unwrap_or_else(std::sync::PoisonError::into_inner)
                .disabled
    }

    fn read_snapshot(&self) -> Result<RyzenAdjSnapshot> {
        self.with_session(Session::read_snapshot)
    }

    fn apply_scheme(&self, scheme: PowerScheme) -> Result<()> {
        self.with_session(|session| session.apply_scheme(scheme))
    }
}

fn decode_power_table(words: [u64; 3]) -> Result<RyzenAdjSnapshot> {
    // PM 表前 24 字节与 RyzenAdj 的六个功耗字段一致，单位为 W。
    let mut values = [0.0; 6];
    for (index, word) in words.into_iter().enumerate() {
        values[index * 2] = f64::from(f32::from_bits(word as u32));
        values[index * 2 + 1] = f64::from(f32::from_bits((word >> 32) as u32));
    }
    if values
        .iter()
        .any(|value| !value.is_finite() || *value < 0.0)
        || [values[0], values[2], values[4]].contains(&0.0)
    {
        return Err(ryzen_error("PawnIO returned invalid power telemetry"));
    }
    Ok(RyzenAdjSnapshot {
        stapm_limit: values[0],
        stapm_value: values[1],
        fast_limit: values[2],
        fast_value: values[3],
        slow_limit: values[4],
        slow_value: values[5],
    })
}

#[cfg(windows)]
use windows_impl::Session;

#[cfg(windows)]
mod windows_impl {
    use super::*;
    use std::{
        ffi::c_void,
        io,
        os::windows::io::{AsRawHandle, FromRawHandle, OwnedHandle},
        ptr::{null, null_mut},
    };
    use windows_sys::{
        core::w,
        Win32::{
            Foundation::{
                GENERIC_READ, GENERIC_WRITE, HANDLE, INVALID_HANDLE_VALUE, WAIT_ABANDONED,
                WAIT_OBJECT_0, WAIT_TIMEOUT,
            },
            Storage::FileSystem::{CreateFileW, FILE_SHARE_READ, FILE_SHARE_WRITE, OPEN_EXISTING},
            System::{
                Threading::{
                    CreateMutexExW, ReleaseMutex, WaitForSingleObject, MUTEX_MODIFY_STATE,
                    SYNCHRONIZATION_SYNCHRONIZE,
                },
                IO::DeviceIoControl,
            },
        },
    };

    const MODULE: &[u8] = include_bytes!("../../../tools/PawnIO/Modules/RyzenSMU.bin");
    const IOCTL_LOAD: u32 = (41394 << 16) | (0x821 << 2);
    const IOCTL_EXECUTE: u32 = (41394 << 16) | (0x841 << 2);
    const STRIX_HALO_CODE_NAME: u64 = 32;

    pub(super) struct Session {
        device: OwnedHandle,
        pci_mutex: OwnedHandle,
    }

    struct PciGuard<'a>(&'a OwnedHandle);
    impl Drop for PciGuard<'_> {
        fn drop(&mut self) {
            // SAFETY: 仅成功获得互斥量后创建 guard，且持有句柄引用。
            unsafe {
                ReleaseMutex(self.0.as_raw_handle() as HANDLE);
            }
        }
    }

    fn lock_pci(handle: &OwnedHandle) -> Result<PciGuard<'_>> {
        // 与 LHM、HWiNFO、UXTU 使用同一个全局 PCI 互斥量；不忙等。
        // SAFETY: 句柄是已打开的 Windows mutex。
        match unsafe { WaitForSingleObject(handle.as_raw_handle() as HANDLE, 100) } {
            WAIT_OBJECT_0 | WAIT_ABANDONED => Ok(PciGuard(handle)),
            WAIT_TIMEOUT => Err(ryzen_error("PawnIO PCI mutex busy (100ms limit)")),
            _ => Err(ryzen_error(format!(
                "PawnIO PCI mutex: {}",
                io::Error::last_os_error()
            ))),
        }
    }

    fn ioctl(device: &OwnedHandle, code: u32, input: &[u8], output: &mut [u64]) -> Result<()> {
        let mut returned = 0;
        // SAFETY: 同步 IO，两个切片在调用期间有效且长度准确；输出按 u64 对齐。
        let ok = unsafe {
            DeviceIoControl(
                device.as_raw_handle() as HANDLE,
                code,
                if input.is_empty() {
                    null()
                } else {
                    input.as_ptr().cast::<c_void>()
                },
                input.len() as u32,
                if output.is_empty() {
                    null_mut()
                } else {
                    output.as_mut_ptr().cast::<c_void>()
                },
                std::mem::size_of_val(output) as u32,
                &mut returned,
                null_mut(),
            )
        };
        if ok == 0 {
            return Err(ryzen_error(format!(
                "PawnIO DeviceIoControl: {}",
                io::Error::last_os_error()
            )));
        }
        if returned as usize != std::mem::size_of_val(output) {
            return Err(ryzen_error(format!(
                "PawnIO response size mismatch: {returned}"
            )));
        }
        Ok(())
    }

    fn execute(
        device: &OwnedHandle,
        name: &str,
        arguments: &[u64],
        output: &mut [u64],
    ) -> Result<()> {
        // 官方 ABI：32 字节函数名 + 最多 7 个 64 位参数；每次轮询无堆分配。
        let mut request = [0_u8; 88];
        if name.len() >= 32 || arguments.len() > 7 {
            return Err(ryzen_error("Invalid PawnIO request"));
        }
        request[..name.len()].copy_from_slice(name.as_bytes());
        for (index, argument) in arguments.iter().enumerate() {
            request[32 + index * 8..40 + index * 8].copy_from_slice(&argument.to_le_bytes());
        }
        ioctl(
            device,
            IOCTL_EXECUTE,
            &request[..32 + arguments.len() * 8],
            output,
        )
    }

    impl Session {
        pub(super) fn open() -> Result<Self> {
            // SAFETY: 常量路径以 NUL 结束，不创建文件，只打开已安装的驱动。
            let device = unsafe {
                CreateFileW(
                    w!(r"\\?\GLOBALROOT\Device\PawnIO"),
                    GENERIC_READ | GENERIC_WRITE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE,
                    null(),
                    OPEN_EXISTING,
                    0,
                    null_mut(),
                )
            };
            if device == INVALID_HANDLE_VALUE {
                return Err(ryzen_error(format!(
                    "Open PawnIO (install signed PawnIO and run elevated): {}",
                    io::Error::last_os_error()
                )));
            }
            // SAFETY: CreateFileW 成功返回独占管理的有效句柄。
            let device = unsafe { OwnedHandle::from_raw_handle(device) };
            // SAFETY: 仅申请获取/释放所需权限，允许复用 LHM 创建的全局互斥量。
            let mutex = unsafe {
                CreateMutexExW(
                    null(),
                    w!(r"Global\Access_PCI"),
                    0,
                    MUTEX_MODIFY_STATE | SYNCHRONIZATION_SYNCHRONIZE,
                )
            };
            if mutex.is_null() {
                return Err(ryzen_error(format!(
                    "Open PCI mutex: {}",
                    io::Error::last_os_error()
                )));
            }
            // SAFETY: 成功创建/打开的 mutex 句柄只在这里转移一次所有权。
            let pci_mutex = unsafe { OwnedHandle::from_raw_handle(mutex) };
            {
                let _guard = lock_pci(&pci_mutex)?;
                ioctl(&device, IOCTL_LOAD, MODULE, &mut [])?;
                let mut code_name = [0];
                execute(&device, "ioctl_get_code_name", &[], &mut code_name)?;
                if code_name[0] != STRIX_HALO_CODE_NAME {
                    return Err(ryzen_error("PawnIO power control requires Strix Halo"));
                }
                let mut table = [0; 2];
                execute(&device, "ioctl_resolve_pm_table", &[], &mut table)?;
                if table[0] == 0 || table[1] == 0 {
                    return Err(ryzen_error("PawnIO PM table unavailable"));
                }
                tracing::info!(
                    table_version = table[0],
                    "PawnIO Strix Halo power backend initialized"
                );
            }
            Ok(Self { device, pci_mutex })
        }

        pub(super) fn read_snapshot(&mut self) -> Result<RyzenAdjSnapshot> {
            let _guard = lock_pci(&self.pci_mutex)?;
            // 每次采样只刷新一次、读取 24 字节，不扫描存储或其他 PCI 设备。
            execute(&self.device, "ioctl_update_pm_table", &[], &mut [])?;
            let mut words = [0; 3];
            execute(&self.device, "ioctl_read_pm_table", &[], &mut words)?;
            decode_power_table(words)
        }

        pub(super) fn apply_scheme(&mut self, scheme: PowerScheme) -> Result<()> {
            let limits = [scheme.stapm_watts, scheme.fast_watts, scheme.slow_watts];
            if limits.iter().any(|limit| *limit <= 0) {
                return Err(ryzen_error("Power limits must be positive"));
            }
            let _guard = lock_pci(&self.pci_mutex)?;
            // 官方模块使用 RSMU；UXTU 的 Strix Halo 命令表同为 0x31/0x32/0x33。
            for (command, watts) in [0x31, 0x32, 0x33].into_iter().zip(limits) {
                let args = [command, (watts as u64) * 1000, 0, 0, 0, 0, 0];
                execute(&self.device, "ioctl_send_smu_command", &args, &mut [0; 6])?;
            }
            Ok(())
        }
    }
}

#[cfg(not(windows))]
struct Session;
#[cfg(not(windows))]
impl Session {
    fn open() -> Result<Self> {
        Err(ryzen_error("PawnIO is only available on Windows"))
    }
    fn read_snapshot(&mut self) -> Result<RyzenAdjSnapshot> {
        Err(ryzen_error("PawnIO is only available on Windows"))
    }
    fn apply_scheme(&mut self, _: PowerScheme) -> Result<()> {
        Err(ryzen_error("PawnIO is only available on Windows"))
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn words(values: [f32; 6]) -> [u64; 3] {
        std::array::from_fn(|i| {
            u64::from(values[i * 2].to_bits()) | (u64::from(values[i * 2 + 1].to_bits()) << 32)
        })
    }
    #[test]
    fn pm_table_decodes_watts_without_changing_scale() {
        let value = decode_power_table(words([85.0, 24.5, 120.0, 35.0, 85.0, 27.0])).unwrap();
        assert_eq!(
            (
                value.stapm_limit,
                value.stapm_value,
                value.fast_limit,
                value.fast_value,
                value.slow_limit,
                value.slow_value
            ),
            (85.0, 24.5, 120.0, 35.0, 85.0, 27.0)
        );
    }
    #[test]
    fn pm_table_rejects_invalid_limits_and_nonfinite_telemetry() {
        for values in [
            [0.0; 6],
            [85.0, f32::NAN, 120.0, 35.0, 85.0, 27.0],
            [85.0, 24.5, 120.0, f32::INFINITY, 85.0, 27.0],
            [-1.0, 24.5, 120.0, 35.0, 85.0, 27.0],
        ] {
            assert!(decode_power_table(words(values)).is_err());
        }
        assert_eq!(
            decode_power_table(words([85.0, 0.0, 120.0, 0.0, 85.0, 0.0]))
                .unwrap()
                .stapm_value,
            0.0
        );
    }
}
