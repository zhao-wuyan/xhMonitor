// 国际化文本映射
export const i18n = {
  zh: {
    // 页面标题
    appTitle: '星核监视器',
    appSubtitle: 'Windows 资源监控器',
    appVersion: 'v0.2.5',

    // 连接状态
    connected: '已连接',
    disconnected: '未连接',
    online: '在线',
    offline: '离线',
    reconnecting: '重新连接中...',
    connectionClosed: '连接已关闭',

    // 导航/通用
    Monitor: '监控',
    Hardware: '硬件',
    Settings: '设置',
    'Open settings': '打开设置',
    'Close settings': '关闭设置',
    'Restore Defaults': '恢复默认',
    Save: '保存',
    Clear: '清除',

    // 安全
    Security: '安全',
    'Access Key': '访问密钥',
    'Access key hint': '启用“局域网访问密钥”后，在这里填写密钥，才可以访问数据接口与实时指标。',
    'Access Key Required': '需要填写访问密钥',
    'Enter access key': '请输入访问密钥',

    // 指标名称
    CPU: 'CPU',
    RAM: 'RAM',
    GPU: 'GPU',
    VRAM: 'VRAM',
    NET: 'NET',
    PWR: 'PWR',
    'CPU Usage': 'CPU 使用率',
    'Memory Usage': '内存使用',
    'GPU Usage': 'GPU 使用率',
    'VRAM Usage': '显存使用',

    // 通用文本
    processes: '个进程',
    'Drag to reorder': '拖拽排序',
    'Total CPU': 'CPU 总计',
    'Total Memory': '内存总计',
    'Total GPU': 'GPU 总计',
    'Total VRAM': '显存总计',

    // 设置面板
    'Layout Settings': '布局设置',
    'Grid Columns': '网格列数',
    'Grid Gap': '网格间距',
    'Card Drag Mode': '拖拽模式',
    'Sort (Reorder)': '排序（调整顺序）',
    'Swap (Drop)': '交换（松开交换）',
    Current: '当前',
    Visibility: '显示',
    Header: '顶部栏',
    Disk: '磁盘',
    Cards: '卡片',
    Background: '背景',
    Gradient: '渐变',
    'Background Image': '背景图片',
    'Choose Image': '选择图片',
    'Remove Image': '移除图片',
    Blur: '模糊',
    Mask: '遮罩',
    Opacity: '透明度',
    'Panel Opacity': '面板透明度',
    'Theme Colors': '主题颜色',

    // 进程监控
    'Process Monitor': '进程监控',
    'Search processes...': '搜索进程...',
    Process: '进程',
    PID: '进程ID',

    // 加载状态
    'Loading configuration...': '正在加载配置...',
    'Failed to load metric configuration': '加载指标配置失败，请检查后端是否运行',
    'Waiting for metrics data...': '等待指标数据...',

    // 搜索结果
    'No processes found matching': '未找到匹配的进程',
  },

  en: {
    // 页面标题
    appTitle: 'XhMonitor',
    appSubtitle: 'Windows Resource Monitor',
    appVersion: 'v0.2.4',

    // 连接状态
    connected: 'Connected',
    disconnected: 'Disconnected',
    online: 'Online',
    offline: 'Offline',
    reconnecting: 'Reconnecting...',
    connectionClosed: 'Connection closed',

    // 导航/通用
    Monitor: 'Monitor',
    Hardware: 'Hardware',
    Settings: 'Settings',
    'Open settings': 'Open settings',
    'Close settings': 'Close settings',
    'Restore Defaults': 'Restore Defaults',
    Save: 'Save',
    Clear: 'Clear',

    // Security
    Security: 'Security',
    'Access Key': 'Access Key',
    'Access key hint': 'If LAN access key is enabled, enter the key here to access APIs and realtime metrics.',
    'Access Key Required': 'Access key required',
    'Enter access key': 'Enter access key',

    // 指标名称
    CPU: 'CPU',
    RAM: 'RAM',
    GPU: 'GPU',
    VRAM: 'VRAM',
    NET: 'NET',
    PWR: 'PWR',
    'CPU Usage': 'CPU Usage',
    'Memory Usage': 'Memory Usage',
    'GPU Usage': 'GPU Usage',
    'VRAM Usage': 'VRAM Usage',

    // 通用文本
    processes: 'processes',
    'Drag to reorder': 'Drag to reorder',
    'Total CPU': 'Total CPU',
    'Total Memory': 'Total Memory',
    'Total GPU': 'Total GPU',
    'Total VRAM': 'Total VRAM',

    // 设置面板
    'Layout Settings': 'Layout Settings',
    'Grid Columns': 'Grid Columns',
    'Grid Gap': 'Grid Gap',
    'Card Drag Mode': 'Card Drag Mode',
    'Sort (Reorder)': 'Sort (Reorder)',
    'Swap (Drop)': 'Swap (Drop)',
    Current: 'Current',
    Visibility: 'Visibility',
    Header: 'Header',
    Disk: 'Disk',
    Cards: 'Cards',
    Background: 'Background',
    Gradient: 'Gradient',
    'Background Image': 'Background Image',
    'Choose Image': 'Choose Image',
    'Remove Image': 'Remove Image',
    Blur: 'Blur',
    Mask: 'Mask',
    Opacity: 'Opacity',
    'Panel Opacity': 'Panel Opacity',
    'Theme Colors': 'Theme Colors',

    // 进程监控
    'Process Monitor': 'Process Monitor',
    'Search processes...': 'Search processes...',
    Process: 'Process',
    PID: 'PID',

    // 加载状态
    'Loading configuration...': 'Loading configuration...',
    'Failed to load metric configuration': 'Failed to load metric configuration',
    'Waiting for metrics data...': 'Waiting for metrics data...',

    // 搜索结果
    'No processes found matching': 'No processes found matching',
  },
};

// 当前语言
let currentLocale: 'zh' | 'en' = 'zh';

// 翻译函数
export const t = (key: string): string => {
  return i18n[currentLocale][key as keyof typeof i18n.zh] || key;
};

// 切换语言
export const setLocale = (locale: 'zh' | 'en') => {
  currentLocale = locale;
};

// 获取当前语言
export const getLocale = () => currentLocale;
