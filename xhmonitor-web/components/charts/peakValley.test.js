import assert from 'node:assert/strict';
import { test } from 'node:test';

import {
  DEFAULT_PEAK_VALLEY_CONFIG,
  computeProminence,
  computeSeriesStats,
  filterSignificantExtrema,
  findExtremaCandidates,
  pickFallbackMarker,
  selectMarkerIdsToKeep,
} from './peakValley.js';

test('computeSeriesStats: range/noise', () => {
  const data = [0, 1, 2, 1, 0];
  const stats = computeSeriesStats(data, { ...DEFAULT_PEAK_VALLEY_CONFIG, noiseQuantile: 0.5 });
  assert.equal(stats.min, 0);
  assert.equal(stats.max, 2);
  assert.equal(stats.range, 2);
  assert.equal(stats.noise, 1);
});

test('findExtremaCandidates: strict max/min', () => {
  const maxData = [1, 2, 3, 2, 1];
  const maxCandidates = findExtremaCandidates(maxData);
  assert.deepEqual(maxCandidates, [{ index: 2, value: 3, type: 'max' }]);

  const minData = [3, 2, 1, 2, 3];
  const minCandidates = findExtremaCandidates(minData);
  assert.deepEqual(minCandidates, [{ index: 2, value: 1, type: 'min' }]);
});

test('findExtremaCandidates: plateau max/min', () => {
  const plateauMax = [1, 3, 3, 3, 1];
  const plateauMaxCandidates = findExtremaCandidates(plateauMax);
  assert.deepEqual(plateauMaxCandidates, [{ index: 2, value: 3, type: 'max' }]);

  const plateauMin = [3, 1, 1, 1, 3];
  const plateauMinCandidates = findExtremaCandidates(plateauMin);
  assert.deepEqual(plateauMinCandidates, [{ index: 2, value: 1, type: 'min' }]);
});

test('computeProminence: peak/valley', () => {
  const peakData = [1, 2, 5, 2, 1];
  assert.equal(computeProminence(peakData, 2, 'max', 4), 4);

  const valleyData = [5, 2, 1, 2, 5];
  assert.equal(computeProminence(valleyData, 2, 'min', 4), 4);
});

test('filterSignificantExtrema: keeps prominent extrema', () => {
  const data = [1, 2, 5, 2, 1];
  const candidates = findExtremaCandidates(data);
  const stats = computeSeriesStats(data, DEFAULT_PEAK_VALLEY_CONFIG);
  const significant = filterSignificantExtrema(candidates, data, stats, DEFAULT_PEAK_VALLEY_CONFIG);
  assert.equal(significant.length, 1);
  assert.equal(significant[0].type, 'max');
  assert.equal(significant[0].index, 2);
  assert.ok(significant[0].prominence > 0);
});

test('selectMarkerIdsToKeep: limits per type and enforces min distance', () => {
  const points = Array.from({ length: 10 }, (_, i) => ({ x: i * 10, y: 0 }));

  const markers = [
    { id: 1, type: 'max', index: 9, prominence: 5 },
    { id: 2, type: 'max', index: 8, prominence: 4 }, // too close to id:1
    { id: 3, type: 'max', index: 6, prominence: 1 }, // far enough from id:1
    { id: 4, type: 'min', index: 9, prominence: 2 },
    { id: 5, type: 'min', index: 4, prominence: 3 },
    { id: 6, type: 'min', index: 2, prominence: 9 }, // should be dropped by keepAfterXRatio
  ];

  const keepIds = selectMarkerIdsToKeep(markers, points, {
    ...DEFAULT_PEAK_VALLEY_CONFIG,
    maxPerType: 2,
    minXDistancePx: 25,
    keepAfterXRatio: 0.35,
    recencyWeight: 0,
  });

  assert.deepEqual(keepIds.sort((a, b) => a - b), [1, 3, 4, 5]);
});

test('pickFallbackMarker: prefers extrema in visible region', () => {
  const data = [1, 2, 3, 2, 1, 2, 6, 2, 1, 1];
  const points = Array.from({ length: data.length }, (_, i) => ({ x: i * 10, y: 0 }));
  const stats = computeSeriesStats(data, DEFAULT_PEAK_VALLEY_CONFIG);

  const marker = pickFallbackMarker(data, points, stats, {
    ...DEFAULT_PEAK_VALLEY_CONFIG,
    keepAfterXRatio: 0.35,
    recencyWeight: 0,
  });

  assert.ok(marker);
  assert.equal(marker.type, 'max');
  assert.equal(marker.index, 6);
  assert.equal(marker.value, 6);
  assert.ok(marker.prominence > 0);
});

test('pickFallbackMarker: monotonic selects extreme in visible region', () => {
  const data = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];
  const points = Array.from({ length: data.length }, (_, i) => ({ x: i * 10, y: 0 }));
  const stats = computeSeriesStats(data, DEFAULT_PEAK_VALLEY_CONFIG);

  const marker = pickFallbackMarker(data, points, stats, {
    ...DEFAULT_PEAK_VALLEY_CONFIG,
    keepAfterXRatio: 0.35,
    recencyWeight: 0,
  });

  assert.ok(marker);
  assert.equal(marker.type, 'max');
  assert.equal(marker.index, 9);
  assert.equal(marker.value, 10);
  assert.ok(marker.prominence > 0);
});

test('pickFallbackMarker: returns null when visible region has only invalid/zero values', () => {
  const data = [0, 0, 0, 0, 0, 0];
  const points = Array.from({ length: data.length }, (_, i) => ({ x: i * 10, y: 0 }));
  const stats = computeSeriesStats(data, DEFAULT_PEAK_VALLEY_CONFIG);

  const marker = pickFallbackMarker(data, points, stats, {
    ...DEFAULT_PEAK_VALLEY_CONFIG,
    keepAfterXRatio: 0.35,
    recencyWeight: 0,
  });

  assert.equal(marker, null);
});
