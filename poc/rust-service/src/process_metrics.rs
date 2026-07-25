// Rust Service POC — src/process_metrics.rs
// 进程级指标采集（windows crate，与 LHM bridge 互不干涉）
// POC 只做最简枚举验证：进程名 + PID + 工作集大小

use windows::Win32::{
    Foundation::CloseHandle,
    System::{
        Diagnostics::ToolHelp::{
            CreateToolhelp32Snapshot, Process32FirstW, Process32NextW,
            PROCESSENTRY32W, TH32CS_SNAPPROCESS,
        },
        ProcessStatus::{GetProcessMemoryInfo, PROCESS_MEMORY_COUNTERS},
        Threading::{OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION},
    },
};

#[derive(Debug, Clone)]
pub struct ProcessSnapshot {
    pub pid:           u32,
    pub name:          String,
    pub working_set_mb: f64,
}

/// 枚举所有进程，返回名称 + 内存快照
/// 仅用于验证 windows crate 进程级 API 可用；生产版本会用 PDH per-process counters
pub fn collect() -> Vec<ProcessSnapshot> {
    let mut result = Vec::new();

    unsafe {
        let snap = match CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) {
            Ok(h) => h,
            Err(e) => {
                tracing::error!("[process_metrics] CreateToolhelp32Snapshot failed: {e}");
                return result;
            }
        };

        let mut entry = PROCESSENTRY32W::default();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;

        if Process32FirstW(snap, &mut entry).is_err() {
            let _ = CloseHandle(snap);
            return result;
        }

        loop {
            let name = String::from_utf16_lossy(
                &entry.szExeFile[..entry.szExeFile.iter().position(|&c| c == 0).unwrap_or(260)]
            );

            let working_set_mb = get_working_set(entry.th32ProcessID);

            result.push(ProcessSnapshot {
                pid: entry.th32ProcessID,
                name,
                working_set_mb,
            });

            if Process32NextW(snap, &mut entry).is_err() {
                break;
            }
        }

        let _ = CloseHandle(snap);
    }

    result
}

fn get_working_set(pid: u32) -> f64 {
    unsafe {
        let Ok(handle) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) else {
            return 0.0;
        };
        let mut pmc = PROCESS_MEMORY_COUNTERS::default();
        pmc.cb = std::mem::size_of::<PROCESS_MEMORY_COUNTERS>() as u32;
        let mb = if GetProcessMemoryInfo(handle, &mut pmc, pmc.cb).is_ok() {
            pmc.WorkingSetSize as f64 / 1_048_576.0
        } else {
            0.0
        };
        let _ = CloseHandle(handle);
        mb
    }
}
