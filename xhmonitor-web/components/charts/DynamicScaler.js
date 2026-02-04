/**
 * DynamicScaler - 动态缩放控制器
 * 用于网络流量等波动较大的指标，自动调整 Y 轴上限以适应数据范围
 *
 * @class DynamicScaler
 * @param {number} initialMax - 初始上限值（默认 1024）
 * @param {number} shrinkDelay - 缩小延迟时间（默认 3000ms）
 *
 * @example
 * const netScaler = new DynamicScaler(20480, 3000); // 初始 20MB, 3秒延迟
 * const currentMax = netScaler.update(dataArray);
 * chart.draw(dataArray, currentMax);
 */

class DynamicScaler {
  constructor(initialMax = 1024, shrinkDelay = 3000) {
    this.currentMax = initialMax;
    this.shrinkDelay = shrinkDelay;
    this.lowUsageStartTime = null;
    this.minFloor = 10; // 最小底线，防止缩放到 0
  }

  /**
   * 更新缩放上限
   * @param {Array<number>} data - 数据数组
   * @returns {number} 当前上限值
   */
  update(data) {
    let maxInWindow = 0;
    for (const v of data) {
      if (v > maxInWindow) maxInWindow = v;
    }

    // 设定最小底线，防止缩放到 0
    if (maxInWindow < this.minFloor) maxInWindow = this.minFloor;

    // 目标上限：让最大值处于 90% 高度
    const targetMax = maxInWindow / 0.9;

    // 1. 立即拔高：当当前值超过当前上限的 90% (即超过了安全区)
    if (targetMax > this.currentMax) {
      this.currentMax = targetMax;
      this.lowUsageStartTime = null; // 重置缩小计时器
    }
    // 2. 延迟缩小：当前窗口最大值不到上限的 60%
    else if (maxInWindow < this.currentMax * 0.6) {
      if (!this.lowUsageStartTime) {
        this.lowUsageStartTime = Date.now();
      } else {
        const elapsed = Date.now() - this.lowUsageStartTime;
        if (elapsed > this.shrinkDelay) {
          // 平滑过渡：每次更新向目标值逼近 (Lerp 0.2)
          // 这样视觉上会有"缓缓下降"的效果，而不是突变
          this.currentMax = this.currentMax + (targetMax - this.currentMax) * 0.2;
        }
      }
    } else {
      // 在 60% - 90% 之间，保持稳定
      this.lowUsageStartTime = null;
    }

    return this.currentMax;
  }

  /**
   * 重置缩放器
   * @param {number} newMax - 新的上限值（可选）
   */
  reset(newMax) {
    if (newMax !== undefined) {
      this.currentMax = newMax;
    }
    this.lowUsageStartTime = null;
  }

  /**
   * 获取当前上限
   * @returns {number} 当前上限值
   */
  getCurrentMax() {
    return this.currentMax;
  }

  /**
   * 设置最小底线
   * @param {number} floor - 最小底线值
   */
  setMinFloor(floor) {
    this.minFloor = floor;
  }
}

// 导出为全局变量（用于浏览器环境）
if (typeof window !== 'undefined') {
  window.DynamicScaler = DynamicScaler;
}

// 导出为模块（用于 Node.js 环境）
if (typeof module !== 'undefined' && module.exports) {
  module.exports = DynamicScaler;
}
