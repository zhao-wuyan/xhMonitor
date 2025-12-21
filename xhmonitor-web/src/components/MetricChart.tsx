import { useEffect, useRef } from 'react';
import * as echarts from 'echarts';
import type { ChartDataPoint } from '../types';

interface MetricChartProps {
  data: ChartDataPoint[];
  metricId: string;
  title: string;
  unit: string;
  color: string;
}

export const MetricChart = ({ data, metricId, title, unit, color }: MetricChartProps) => {
  const chartRef = useRef<HTMLDivElement>(null);
  const chartInstance = useRef<echarts.ECharts | null>(null);

  useEffect(() => {
    if (!chartRef.current) return;

    // 初始化图表实例
    if (!chartInstance.current) {
      chartInstance.current = echarts.init(chartRef.current, 'dark');
    }

    const chart = chartInstance.current;

    const option: echarts.EChartsOption = {
      title: {
        text: title,
        textStyle: {
          color: '#f3f4f6',
          fontSize: 16,
          fontWeight: 'bold',
        },
      },
      tooltip: {
        trigger: 'axis',
        backgroundColor: 'rgba(17, 24, 39, 0.9)',
        borderColor: 'rgba(255, 255, 255, 0.1)',
        textStyle: {
          color: '#f3f4f6',
        },
        formatter: (params: any) => {
          const param = params[0];
          return `${param.name}<br/>${param.seriesName}: ${param.value}${unit}`;
        },
      },
      grid: {
        left: '3%',
        right: '4%',
        bottom: '3%',
        containLabel: true,
      },
      xAxis: {
        type: 'category',
        boundaryGap: false,
        data: data.map((d) => d.timestamp),
        axisLine: {
          lineStyle: {
            color: '#4b5563',
          },
        },
        axisLabel: {
          color: '#9ca3af',
        },
      },
      yAxis: {
        type: 'value',
        axisLine: {
          lineStyle: {
            color: '#4b5563',
          },
        },
        axisLabel: {
          color: '#9ca3af',
          formatter: `{value}${unit}`,
        },
        splitLine: {
          lineStyle: {
            color: '#374151',
          },
        },
      },
      series: [
        {
          name: title,
          type: 'line',
          smooth: true,
          symbol: 'circle',
          symbolSize: 6,
          lineStyle: {
            color: color,
            width: 2,
          },
          itemStyle: {
            color: color,
          },
          areaStyle: {
            color: new echarts.graphic.LinearGradient(0, 0, 0, 1, [
              {
                offset: 0,
                color: color.replace(')', ', 0.5)').replace('rgb', 'rgba'),
              },
              {
                offset: 1,
                color: color.replace(')', ', 0.05)').replace('rgb', 'rgba'),
              },
            ]),
          },
          data: data.map((d) => d.value),
        },
      ],
    };

    chart.setOption(option);

    const handleResize = () => {
      chart?.resize();
    };

    window.addEventListener('resize', handleResize);

    // 清理函数
    return () => {
      window.removeEventListener('resize', handleResize);
      // 组件卸载时才销毁实例
      if (chartInstance.current) {
        chartInstance.current.dispose();
        chartInstance.current = null;
      }
    };
  }, [data, metricId, title, unit, color]);

  return <div ref={chartRef} className="w-full h-80" />;
};
