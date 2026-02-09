# 优化前后对比报告（480 秒同口径）

## 数据来源

- 优化前（安装目录）：
  - `C:/my_program/XhMonitor`
  - 采样结果：`C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/compare-before-20260209-161838/summary.json`
- 优化后（当前工作区构建）：
  - `C:/ProjectDev/project/xinghe/xhMonitor`
  - 采样结果：`C:/ProjectDev/project/xinghe/xhMonitor/.workflow/.analysis/ANL-desktop-service-memory-optimization-2026-02-08/compare-after-20260209-162821/summary.json`

## 结论

在相同采样窗口（480 秒）下，优化后相较优化前，`service` 与 `desktop` 的内存占用均出现下降，其中 `service` 下降更明显，说明本轮优化已经产生实质收益。

## 指标对比

### Service

- WorkingSet 平均值：`233.468 MB -> 191.392 MB`（`-42.076 MB`, `-18.02%`）
- WorkingSet 峰值：`262.766 MB -> 200.379 MB`（`-62.387 MB`）
- Private 平均值：`138.474 MB -> 109.315 MB`（`-29.159 MB`, `-21.06%`）

### Desktop

- WorkingSet 平均值：`239.558 MB -> 221.96 MB`（`-17.598 MB`, `-7.35%`）
- WorkingSet 峰值：`250.121 MB -> 234.348 MB`（`-15.773 MB`）
- Private 平均值：`142.953 MB -> 134.156 MB`（`-8.797 MB`, `-6.15%`）

## 口径与限制

- 优化前 `service` 进程拒绝 `dotnet-counters` 连接（权限拒绝），因此前后可严格对比的是 `WorkingSet/PrivateMemory` 等进程级指标。
- 当前是 `480 秒` 短窗结果，适合判断“优化是否有效”；若要判断“长稳是否持续改善”，建议补 `30-60 分钟` 同口径采样。
