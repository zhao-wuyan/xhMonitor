/**
 * 峰谷检测与筛选工具（纯函数）
 * - 候选极值检测（含平台 plateau 处理）
 * - 显著性（prominence）计算：衡量峰谷的“代表性”
 * - 基于可视区、最小间距与数量上限的标记裁剪
 *
 * 注意：本文件不依赖 DOM，便于单元测试。
 */

const isFiniteNumber = (value) => Number.isFinite(value);

const clamp = (value, min, max) => Math.min(max, Math.max(min, value));

const quantileSorted = (sorted, q) => {
  if (sorted.length === 0) return 0;
  const index = clamp(Math.floor((sorted.length - 1) * q), 0, sorted.length - 1);
  return sorted[index];
};

export const DEFAULT_PEAK_VALLEY_CONFIG = Object.freeze({
  // 显著性计算窗口（左右各取 windowSize 个点，边界自动收缩）
  prominenceWindow: 6,

  // 噪声水平：使用 |diff| 的分位数（越大越保守）
  noiseQuantile: 0.6,

  // 显著性阈值：max( noise * factor, range * factor )
  prominenceNoiseFactor: 3,
  prominenceRangeFactor: 0.12,

  // 视觉裁剪：每种类型最多保留多少个标记
  maxPerType: 2,

  // 同类型标记的最小 X 像素距离（用于避免重叠）
  minXDistancePx: 58,

  // 丢弃过老标记：进入左侧完全透明区域后可直接移除
  keepAfterXRatio: 0.35,

  // 轻微偏向“更靠右”的标记（0~1）
  recencyWeight: 0.15,
});

/**
 * 计算序列统计信息：min/max/range/noise
 * @param {number[]} data
 * @param {typeof DEFAULT_PEAK_VALLEY_CONFIG} config
 */
export const computeSeriesStats = (data, config = DEFAULT_PEAK_VALLEY_CONFIG) => {
  const finite = data.filter(isFiniteNumber);
  if (finite.length === 0) {
    return { min: 0, max: 0, range: 0, noise: 0 };
  }

  let min = finite[0];
  let max = finite[0];
  for (const v of finite) {
    if (v < min) min = v;
    if (v > max) max = v;
  }

  const diffs = [];
  for (let i = 1; i < data.length; i++) {
    const a = data[i - 1];
    const b = data[i];
    if (!isFiniteNumber(a) || !isFiniteNumber(b)) continue;
    diffs.push(Math.abs(b - a));
  }
  diffs.sort((a, b) => a - b);
  const noise = quantileSorted(diffs, config.noiseQuantile);

  return { min, max, range: max - min, noise };
};

/**
 * 找到候选极值点（支持 plateau：一段相等的平顶/平底）
 * @param {number[]} data
 * @returns {{index:number, value:number, type:'max'|'min'}[]}
 */
export const findExtremaCandidates = (data) => {
  const result = [];
  if (data.length < 3) return result;

  let i = 1;
  while (i < data.length - 1) {
    const prev = data[i - 1];
    const curr = data[i];
    const next = data[i + 1];

    if (!isFiniteNumber(prev) || !isFiniteNumber(curr) || !isFiniteNumber(next)) {
      i++;
      continue;
    }

    // plateau：curr === next，向右扩展找到平台末尾
    if (curr === next) {
      let j = i + 1;
      while (j < data.length - 1 && data[j] === curr) j++;

      const left = data[i - 1];
      const right = data[j];

      if (isFiniteNumber(left) && isFiniteNumber(right)) {
        if (curr > left && curr > right) {
          const index = Math.floor((i + (j - 1)) / 2);
          result.push({ index, value: curr, type: 'max' });
        } else if (curr < left && curr < right) {
          const index = Math.floor((i + (j - 1)) / 2);
          result.push({ index, value: curr, type: 'min' });
        }
      }

      i = j;
      continue;
    }

    if (curr > prev && curr > next) {
      result.push({ index: i, value: curr, type: 'max' });
    } else if (curr < prev && curr < next) {
      result.push({ index: i, value: curr, type: 'min' });
    }

    i++;
  }

  return result;
};

/**
 * 计算某个极值点的显著性（prominence）
 * @param {number[]} data
 * @param {number} index
 * @param {'max'|'min'} type
 * @param {number} windowSize
 */
export const computeProminence = (
  data,
  index,
  type,
  windowSize = DEFAULT_PEAK_VALLEY_CONFIG.prominenceWindow
) => {
  const curr = data[index];
  if (!isFiniteNumber(curr)) return 0;

  const start = Math.max(0, index - windowSize);
  const end = Math.min(data.length - 1, index + windowSize);

  if (type === 'max') {
    let leftMin = Infinity;
    let rightMin = Infinity;
    for (let i = start; i <= index; i++) {
      const v = data[i];
      if (!isFiniteNumber(v)) continue;
      if (v < leftMin) leftMin = v;
    }
    for (let i = index; i <= end; i++) {
      const v = data[i];
      if (!isFiniteNumber(v)) continue;
      if (v < rightMin) rightMin = v;
    }
    if (!Number.isFinite(leftMin) || !Number.isFinite(rightMin)) return 0;
    return Math.min(curr - leftMin, curr - rightMin);
  }

  let leftMax = -Infinity;
  let rightMax = -Infinity;
  for (let i = start; i <= index; i++) {
    const v = data[i];
    if (!isFiniteNumber(v)) continue;
    if (v > leftMax) leftMax = v;
  }
  for (let i = index; i <= end; i++) {
    const v = data[i];
    if (!isFiniteNumber(v)) continue;
    if (v > rightMax) rightMax = v;
  }
  if (!Number.isFinite(leftMax) || !Number.isFinite(rightMax)) return 0;
  return Math.min(leftMax - curr, rightMax - curr);
};

/**
 * 根据显著性与噪声阈值过滤候选极值
 * @param {{index:number,value:number,type:'max'|'min'}[]} candidates
 * @param {number[]} data
 * @param {{min:number,max:number,range:number,noise:number}} stats
 * @param {typeof DEFAULT_PEAK_VALLEY_CONFIG} config
 */
export const filterSignificantExtrema = (
  candidates,
  data,
  stats,
  config = DEFAULT_PEAK_VALLEY_CONFIG
) => {
  const threshold = Math.max(
    stats.noise * config.prominenceNoiseFactor,
    stats.range * config.prominenceRangeFactor
  );

  return candidates
    .map((c) => ({
      ...c,
      prominence: computeProminence(data, c.index, c.type, config.prominenceWindow),
    }))
    .filter((c) => c.prominence >= threshold && c.prominence > 0);
};

const scoreMarker = (marker, pointsLength, recencyWeight) => {
  const prominence = isFiniteNumber(marker.prominence) ? marker.prominence : 0;
  const recency =
    pointsLength > 1 && isFiniteNumber(marker.index)
      ? clamp(marker.index / (pointsLength - 1), 0, 1)
      : 0;
  return prominence * (1 + recencyWeight * recency);
};

/**
 * 选择需要保留的标记 ID（用于在小图表上避免重叠与杂乱）
 * @param {{id:number,index:number,type:'max'|'min',prominence?:number}[]} markers
 * @param {{x:number,y:number}[]} points
 * @param {typeof DEFAULT_PEAK_VALLEY_CONFIG} config
 * @returns {number[]} keepIds
 */
export const selectMarkerIdsToKeep = (markers, points, config = DEFAULT_PEAK_VALLEY_CONFIG) => {
  if (points.length === 0) return [];

  const width = points[points.length - 1].x;
  const cutoffX = width * config.keepAfterXRatio;

  const inView = markers.filter((m) => {
    if (!isFiniteNumber(m.index)) return false;
    if (m.index < 0 || m.index >= points.length) return false;
    return points[m.index].x >= cutoffX;
  });

  const pickType = (type) => {
    const candidates = inView
      .filter((m) => m.type === type)
      .sort((a, b) => {
        const sa = scoreMarker(a, points.length, config.recencyWeight);
        const sb = scoreMarker(b, points.length, config.recencyWeight);
        if (sb !== sa) return sb - sa;
        return (b.index ?? 0) - (a.index ?? 0);
      });

    const selected = [];
    for (const m of candidates) {
      if (selected.length >= config.maxPerType) break;
      const x = points[m.index].x;
      const tooClose = selected.some((s) => Math.abs(points[s.index].x - x) < config.minXDistancePx);
      if (tooClose) continue;
      selected.push(m);
    }
    return selected;
  };

  const keep = [...pickType('max'), ...pickType('min')];
  return keep.map((m) => m.id);
};

