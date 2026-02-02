/**
 * MiniChart - 迷你图表引擎
 * 支持左侧渐隐和动态峰谷标注的实时数据可视化组件
 *
 * @class MiniChart
 * @param {string} canvasId - Canvas 元素 ID
 * @param {string} containerId - 容器元素 ID
 * @param {string} color - 图表颜色（十六进制）
 * @param {Function} formatFn - 数值格式化函数
 *
 * @example
 * const chart = new MiniChart('chart-cpu', 'chart-area-cpu', '#3b82f6', v => v.toFixed(0) + '%');
 * chart.draw(dataArray, 100);
 */

class MiniChart {
  constructor(canvasId, containerId, color, formatFn) {
    this.canvas = document.getElementById(canvasId);
    this.container = document.getElementById(containerId);

    if (!this.canvas || !this.container) {
      throw new Error(`MiniChart: Canvas or container not found (${canvasId}, ${containerId})`);
    }

    this.ctx = this.canvas.getContext('2d');
    this.color = color;
    this.formatFn = formatFn || (v => v.toFixed(0) + '%');
    this.markers = []; // 存储峰谷标记 { index, value, type: 'max'|'min', element }

    this.resize();

    // 响应式调整
    let resizeTimeout;
    window.addEventListener('resize', () => {
      clearTimeout(resizeTimeout);
      resizeTimeout = setTimeout(() => this.resize(), 100);
    });
  }

  /**
   * 调整画布尺寸以匹配容器
   */
  resize() {
    const parent = this.canvas.parentElement;
    this.canvas.width = parent.clientWidth;
    this.canvas.height = parent.clientHeight;
  }

  /**
   * 绘制图表
   * @param {Array<number>} data - 数据数组
   * @param {number} maxValue - Y 轴最大值（默认 100）
   */
  draw(data, maxValue = 100) {
    const { width, height } = this.canvas;
    const ctx = this.ctx;

    ctx.clearRect(0, 0, width, height);

    const stepX = width / (data.length - 1);
    const topPadding = 15;
    const bottomPadding = 15;
    const drawHeight = height - topPadding - bottomPadding;

    // 计算所有点的坐标
    const points = data.map((val, index) => ({
      x: index * stepX,
      y: topPadding + drawHeight - (val / maxValue) * drawHeight,
      value: val,
      index: index
    }));

    // 绘制曲线（带渐隐效果）
    ctx.save();

    // 绘制曲线路径
    ctx.beginPath();
    ctx.strokeStyle = this.color;
    ctx.lineWidth = 2.5;
    ctx.lineJoin = 'round';
    ctx.lineCap = 'round';

    points.forEach((pt, i) => {
      if (i === 0) ctx.moveTo(pt.x, pt.y);
      else ctx.lineTo(pt.x, pt.y);
    });
    ctx.stroke();

    // 绘制渐变填充
    ctx.lineTo(width, height);
    ctx.lineTo(0, height);
    ctx.closePath();

    const fillGradient = ctx.createLinearGradient(0, 0, 0, height);
    fillGradient.addColorStop(0, this.hexToRgba(this.color, 0.35));
    fillGradient.addColorStop(1, this.hexToRgba(this.color, 0.0));
    ctx.fillStyle = fillGradient;
    ctx.fill();

    ctx.restore();

    // 左侧50%渐隐 - 使用 destination-out 擦除模式
    ctx.save();
    ctx.globalCompositeOperation = 'destination-out';
    const fadeGradient = ctx.createLinearGradient(0, 0, width * 0.5, 0);
    fadeGradient.addColorStop(0, 'rgba(0, 0, 0, 1)');      // 完全擦除
    fadeGradient.addColorStop(0.6, 'rgba(0, 0, 0, 0.5)');  // 半透明
    fadeGradient.addColorStop(1, 'rgba(0, 0, 0, 0)');      // 不擦除
    ctx.fillStyle = fadeGradient;
    ctx.fillRect(0, 0, width * 0.5, height);
    ctx.restore();

    // 更新峰谷标记
    this.updateMarkers(data, points);
  }

  /**
   * 更新峰谷值标记
   * @param {Array<number>} data - 数据数组
   * @param {Array<Object>} points - 坐标点数组
   */
  updateMarkers(data, points) {
    // 使用改进的算法：只保留显著的峰谷，遵循 峰-谷-峰-谷 交替规律
    const validData = data.filter(v => v > 0);
    if (validData.length < 3) return;

    // 找出所有候选极值点
    const candidates = [];
    for (let i = 1; i < data.length - 1; i++) {
      const prev = data[i - 1];
      const curr = data[i];
      const next = data[i + 1];

      if (curr > prev && curr > next) {
        candidates.push({ index: i, value: curr, type: 'max' });
      } else if (curr < prev && curr < next) {
        candidates.push({ index: i, value: curr, type: 'min' });
      }
    }

    // 过滤：确保峰谷交替，且变化幅度足够大
    const minAmplitude = 5;
    const filtered = [];
    let lastType = null;
    let lastValue = null;

    for (const c of candidates) {
      // 确保峰谷交替
      if (lastType === c.type) {
        // 同类型：保留更极端的那个
        if (filtered.length > 0) {
          const last = filtered[filtered.length - 1];
          if ((c.type === 'max' && c.value > last.value) ||
              (c.type === 'min' && c.value < last.value)) {
            filtered[filtered.length - 1] = c;
            lastValue = c.value;
          }
        }
        continue;
      }

      // 检查与上一个极值的幅度差
      if (lastValue !== null && Math.abs(c.value - lastValue) < minAmplitude) {
        continue;
      }

      filtered.push(c);
      lastType = c.type;
      lastValue = c.value;
    }

    // 清理已移出视图的标记
    this.markers = this.markers.filter(m => {
      if (m.index < 0) {
        m.element.remove();
        return false;
      }
      return true;
    });

    // 更新现有标记的索引（数据左移）
    this.markers.forEach(m => {
      m.index--;
    });

    // 添加新的极值标记（只添加右侧新出现的）
    filtered.forEach(ext => {
      // 检查是否已存在相近的标记
      const exists = this.markers.some(m =>
        Math.abs(m.index - ext.index) < 3 && m.type === ext.type
      );
      if (!exists && ext.index > data.length - 5) {
        // 新峰出现时，移除所有比它小的峰；新谷出现时，移除所有比它大的谷
        this.markers = this.markers.filter(m => {
          if (m.type === ext.type) {
            const shouldRemove = (ext.type === 'max' && ext.value > m.value) ||
                                 (ext.type === 'min' && ext.value < m.value);
            if (shouldRemove) {
              m.element.remove();
              return false;
            }
          }
          return true;
        });
        this.addMarker(ext);
      }
    });

    // 更新所有标记的位置
    const { width } = this.canvas;

    this.markers.forEach(m => {
      if (m.index < 0 || m.index >= points.length) return;

      const pt = points[m.index];

      m.element.style.left = pt.x + 'px';
      m.element.style.top = pt.y + 'px';
      m.element.innerText = this.formatFn(data[m.index]);

      // 左侧50%渐隐区域内的标记也渐隐
      const fadeRatio = Math.min(1, pt.x / (width * 0.5));
      m.element.style.opacity = fadeRatio;
    });
  }

  /**
   * 添加峰谷值标记
   * @param {Object} ext - 极值点对象 { index, value, type }
   */
  addMarker(ext) {
    const el = document.createElement('div');
    el.className = 'xh-chart-peak-marker ' + ext.type;
    el.style.color = ext.type === 'max' ? this.color : '#94a3b8';
    el.innerText = this.formatFn(ext.value);
    this.container.appendChild(el);

    this.markers.push({
      index: ext.index,
      value: ext.value,
      type: ext.type,
      element: el
    });
  }

  /**
   * 十六进制颜色转 RGBA
   * @param {string} hex - 十六进制颜色
   * @param {number} alpha - 透明度 (0-1)
   * @returns {string} RGBA 颜色字符串
   */
  hexToRgba(hex, alpha) {
    let r = 0, g = 0, b = 0;
    if (hex.length === 4) {
      r = parseInt(hex[1] + hex[1], 16);
      g = parseInt(hex[2] + hex[2], 16);
      b = parseInt(hex[3] + hex[3], 16);
    } else if (hex.length === 7) {
      r = parseInt(hex[1] + hex[2], 16);
      g = parseInt(hex[3] + hex[4], 16);
      b = parseInt(hex[5] + hex[6], 16);
    }
    return `rgba(${r},${g},${b},${alpha})`;
  }

  /**
   * 销毁图表实例
   */
  destroy() {
    // 移除所有标记
    this.markers.forEach(m => m.element.remove());
    this.markers = [];

    // 清空画布
    if (this.ctx) {
      this.ctx.clearRect(0, 0, this.canvas.width, this.canvas.height);
    }
  }
}

// 导出为全局变量（用于浏览器环境）
if (typeof window !== 'undefined') {
  window.MiniChart = MiniChart;
}

// 导出为模块（用于 Node.js 环境）
if (typeof module !== 'undefined' && module.exports) {
  module.exports = MiniChart;
}
