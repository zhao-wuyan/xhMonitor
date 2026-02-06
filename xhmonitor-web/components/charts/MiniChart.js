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

import {
  DEFAULT_PEAK_VALLEY_CONFIG,
  computeSeriesStats,
  filterSignificantExtrema,
  findExtremaCandidates,
  pickFallbackMarker,
  selectMarkerIdsToKeep,
} from './peakValley.js';

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
    this.markerId = 0;
    this.markersEnabled = true;

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
    if (!this.markersEnabled) {
      this.clearMarkers();
      return;
    }

    if (!Array.isArray(data) || data.length < 2 || points.length < 2) {
      this.clearMarkers();
      return;
    }

    const width = points[points.length - 1].x;
    const cutoffX = width * DEFAULT_PEAK_VALLEY_CONFIG.keepAfterXRatio;
    let visibleStartIndex = 0;
    while (visibleStartIndex < points.length && points[visibleStartIndex].x < cutoffX) {
      visibleStartIndex++;
    }

    const hasVisibleValidValue = data.some(
      (value, index) => index >= visibleStartIndex && Number.isFinite(value) && value > 0
    );
    if (!hasVisibleValidValue) {
      this.clearMarkers();
      return;
    }

    // 1) 对齐最新窗口：数据每次 pushValue 都会左移 1 格，因此标记索引同步左移
    this.markers = this.markers.filter((m) => {
      m.index -= 1;
      if (m.index < 0) {
        m.element.remove();
        return false;
      }
      return true;
    });

    const stats = computeSeriesStats(data, DEFAULT_PEAK_VALLEY_CONFIG);

    // 2) 仅在右侧新数据区域插入“显著”的新峰谷，减少噪声型标注堆叠
    const candidates = findExtremaCandidates(data);
    const significant = filterSignificantExtrema(candidates, data, stats, DEFAULT_PEAK_VALLEY_CONFIG);

    const rightMostCount = 5;
    const rightStartIndex = Math.max(1, data.length - rightMostCount);
    const newCandidates = significant.filter((e) => e.index >= rightStartIndex);

    // 每种类型只插入一个最显著的（避免一次出现多个造成重叠）
    const bestByType = { max: null, min: null };
    for (const e of newCandidates) {
      const prev = bestByType[e.type];
      if (!prev || e.prominence > prev.prominence) bestByType[e.type] = e;
    }

    for (const e of Object.values(bestByType)) {
      if (!e) continue;

      // 去重：同类型同索引不重复插入
      const hasSame = this.markers.some((m) => m.type === e.type && m.index === e.index);
      if (hasSame) continue;

      // 聚类合并：与同类型附近标记非常接近且不更显著，则跳过
      const clusterIndexDistance = 6;
      const nearby = this.markers.filter(
        (m) => m.type === e.type && Math.abs(m.index - e.index) <= clusterIndexDistance
      );
      if (nearby.length > 0) {
        const bestExisting = nearby.reduce((best, cur) =>
          (cur.prominence ?? 0) > (best.prominence ?? 0) ? cur : best
        );
        const existingProminence = bestExisting.prominence ?? 0;
        if (existingProminence > 0 && e.prominence <= existingProminence * 0.9) continue;
      }

      this.addMarker(e);
    }

    // 3) 视觉裁剪：只保留少量、互不拥挤、且位于可视区域的标记
    const keepIds = new Set(selectMarkerIdsToKeep(this.markers, points, DEFAULT_PEAK_VALLEY_CONFIG));
    this.markers = this.markers.filter((m) => {
      if (keepIds.has(m.id)) return true;
      m.element.remove();
      return false;
    });

    // 兜底：当可视区内没有任何标注时，至少保留 1 个标注（有曲线时）
    if (this.markers.length === 0) {
      const fallback = pickFallbackMarker(data, points, stats, DEFAULT_PEAK_VALLEY_CONFIG);
      if (fallback) {
        this.addMarker(fallback);
      }
    }

    // 4) 更新位置：防裁剪（翻转/夹取）+ 最终防重叠（Drop）
    this.layoutMarkers(data, points);
  }

  clearMarkers() {
    if (!this.markers || this.markers.length === 0) return;
    this.markers.forEach((m) => m.element.remove());
    this.markers = [];
  }

  /**
   * 更新标记位置与可视性（防裁剪 + 防重叠）
   * @param {Array<number>} data - 数据数组
   * @param {Array<Object>} points - 坐标点数组
   */
  layoutMarkers(data, points) {
    const { width, height } = this.canvas;
    const containerWidth = this.container.clientWidth || width;
    const containerHeight = this.container.clientHeight || height;

    const edgePadding = 2;
    const markerMargin = 6;

    // 第一遍：设置文本、夹取到容器内，并在贴边时翻转位置，避免被裁剪
    this.markers.forEach((m) => {
      if (m.index < 0 || m.index >= points.length) return;

      const pt = points[m.index];
      const el = m.element;

      const value = Number.isFinite(data[m.index]) ? data[m.index] : m.value;
      el.innerText = this.formatFn(value);

      el.classList.remove('pos-top', 'pos-bottom');

      const markerWidth = el.offsetWidth;
      const markerHeight = el.offsetHeight;

      let left = pt.x;
      const minLeft = markerWidth / 2 + edgePadding;
      const maxLeft = containerWidth - markerWidth / 2 - edgePadding;
      left = Number.isFinite(left) ? Math.min(maxLeft, Math.max(minLeft, left)) : minLeft;

      const y = pt.y;
      let position = m.type === 'min' ? 'bottom' : 'top';

      const topIfTop = y - markerMargin - markerHeight;
      const topIfBottom = y + markerMargin;

      if (position === 'bottom') {
        if (topIfBottom + markerHeight > containerHeight - edgePadding) position = 'top';
      } else {
        if (topIfTop < edgePadding) position = 'bottom';
      }

      el.classList.add(position === 'top' ? 'pos-top' : 'pos-bottom');
      el.style.left = left + 'px';
      el.style.top = y + 'px';

      // 左侧50%渐隐区域内的标记也渐隐（按曲线点的真实 x 计算）
      const fadeRatio = Math.min(1, pt.x / (width * 0.5));
      el.style.opacity = fadeRatio;
    });

    // 第二遍：最终防重叠（按显著性优先保留）
    const containerRect = this.container.getBoundingClientRect();
    const items = this.markers
      .filter((m) => m.index >= 0 && m.index < points.length)
      .map((m) => {
        const rect = m.element.getBoundingClientRect();
        const recency = m.index / Math.max(1, points.length - 1);
        const priority =
          (Number.isFinite(m.prominence) ? m.prominence : 0) *
          (1 + DEFAULT_PEAK_VALLEY_CONFIG.recencyWeight * recency);

        return {
          marker: m,
          rect: {
            left: rect.left - containerRect.left,
            right: rect.right - containerRect.left,
            top: rect.top - containerRect.top,
            bottom: rect.bottom - containerRect.top,
          },
          priority,
        };
      })
      .sort((a, b) => b.priority - a.priority);

    const kept = [];
    const droppedIds = new Set();
    const overlapPadding = 2;

    const overlaps = (a, b) => {
      return !(
        a.right + overlapPadding < b.left ||
        a.left - overlapPadding > b.right ||
        a.bottom + overlapPadding < b.top ||
        a.top - overlapPadding > b.bottom
      );
    };

    for (const item of items) {
      const hit = kept.some((k) => overlaps(item.rect, k.rect));
      if (hit) droppedIds.add(item.marker.id);
      else kept.push(item);
    }

    if (droppedIds.size > 0) {
      this.markers = this.markers.filter((m) => {
        if (!droppedIds.has(m.id)) return true;
        m.element.remove();
        return false;
      });
    }
  }

  /**
   * 添加峰谷值标记
   * @param {Object} ext - 极值点对象 { index, value, type }
   */
  addMarker(ext) {
    const el = document.createElement('div');
    el.className =
      'xh-chart-peak-marker ' + ext.type + ' ' + (ext.type === 'min' ? 'pos-bottom' : 'pos-top');
    el.style.color = ext.type === 'max' ? this.color : '#94a3b8';
    el.innerText = this.formatFn(ext.value);
    this.container.appendChild(el);

    this.markers.push({
      id: ++this.markerId,
      index: ext.index,
      value: ext.value,
      type: ext.type,
      prominence: ext.prominence ?? 0,
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

// ES6 默认导出
export default MiniChart;

// 导出为全局变量（用于浏览器环境）
if (typeof window !== 'undefined') {
  window.MiniChart = MiniChart;
}

// 导出为模块（用于 Node.js 环境）
if (typeof module !== 'undefined' && module.exports) {
  module.exports = MiniChart;
}
