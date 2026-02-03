// 国际化文本映射
export const i18n = {
  zh: {
    // 页面标题
    appTitle: 'XhMonitor',
    appSubtitle: 'Windows 资源监控器',
    appVersion: 'v0.2.4',

    // 连接状态
    connected: '已连接',
    disconnected: '未连接',
    online: 'Online',
    offline: 'Offline',
    reconnecting: '重新连接中...',
    connectionClosed: '连接已关闭',

    // 指标名称
    'CPU Usage': 'CPU 使用率',
    'Memory Usage': '内存使用',
    'GPU Usage': 'GPU 使用率',
    'VRAM Usage': '显存使用',

    // 通用文本
    processes: '个进程',
    'Total CPU': 'CPU 总计',
    'Total Memory': '内存总计',
    'Total GPU': 'GPU 总计',
    'Total VRAM': '显存总计',

    // 进程监控
    'Process Monitor': '进程监控',
    'Search processes...': '搜索进程...',
    Process: '进程',
    PID: '进程ID',

    // 加载状态
    'Loading configuration...': '正在加载配置...',
    'Failed to load metric configuration': '加载指标配置失败',
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

    // 指标名称
    'CPU Usage': 'CPU Usage',
    'Memory Usage': 'Memory Usage',
    'GPU Usage': 'GPU Usage',
    'VRAM Usage': 'VRAM Usage',

    // 通用文本
    processes: 'processes',
    'Total CPU': 'Total CPU',
    'Total Memory': 'Total Memory',
    'Total GPU': 'Total GPU',
    'Total VRAM': 'Total VRAM',

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
