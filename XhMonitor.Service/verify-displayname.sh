#!/bin/bash
# 验证 DisplayName 功能的完整数据流
# 使用方法: 在服务重启后运行此脚本

DB_PATH="C:/ProjectDev/project/xinghe/xhMonitor/XhMonitor.Service/xhmonitor.db"

echo "=== DisplayName 功能验证 ==="
echo ""

echo "1. 检查表结构"
sqlite3 "$DB_PATH" "PRAGMA table_info(ProcessMetricRecords);" | grep DisplayName
echo ""

echo "2. 统计 DisplayName 数据"
sqlite3 "$DB_PATH" "SELECT
    COUNT(*) as total,
    COUNT(DisplayName) as with_displayname,
    COUNT(*) - COUNT(DisplayName) as null_displayname
FROM ProcessMetricRecords;"
echo ""

echo "3. 查看最新的 10 条记录"
sqlite3 "$DB_PATH" "SELECT
    Id,
    ProcessName,
    COALESCE(DisplayName, '(null)') as DisplayName,
    datetime(Timestamp) as Time
FROM ProcessMetricRecords
ORDER BY Id DESC
LIMIT 10;"
echo ""

echo "4. 查看有 DisplayName 的记录(如果有)"
sqlite3 "$DB_PATH" "SELECT
    Id,
    ProcessName,
    DisplayName,
    datetime(Timestamp) as Time
FROM ProcessMetricRecords
WHERE DisplayName IS NOT NULL
ORDER BY Id DESC
LIMIT 10;"
echo ""

echo "5. 按 DisplayName 分组统计"
sqlite3 "$DB_PATH" "SELECT
    DisplayName,
    COUNT(*) as count
FROM ProcessMetricRecords
WHERE DisplayName IS NOT NULL
GROUP BY DisplayName
ORDER BY count DESC
LIMIT 10;"
echo ""

echo "验证完成!"
