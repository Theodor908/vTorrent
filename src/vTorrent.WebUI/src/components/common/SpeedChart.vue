<script setup lang="ts">
import { computed } from 'vue';
import { Line } from 'vue-chartjs';
import {
  Chart as ChartJS,
  LineElement,
  PointElement,
  LinearScale,
  CategoryScale,
  Filler,
  Tooltip,
  type ChartData,
  type ChartOptions,
} from 'chart.js';
import { formatSpeed } from '@/utils/format';

// ============================================================
// Chart.js registration
// ============================================================

ChartJS.register(LineElement, PointElement, LinearScale, CategoryScale, Filler, Tooltip);

// ============================================================
// Props
// ============================================================

const props = withDefaults(
  defineProps<{
    downloadHistory: number[];
    uploadHistory: number[];
    showDownload?: boolean;
    showUpload?: boolean;
  }>(),
  {
    showDownload: true,
    showUpload: true,
  },
);

// ============================================================
// Chart data and options
// ============================================================

function getCssVar(name: string): string {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

const chartData = computed((): ChartData<'line'> => {
  const length = Math.max(props.downloadHistory.length, props.uploadHistory.length);
  const labels = Array.from({ length }, (_, i) => String(i));

  const datasets = [];

  if (props.showDownload) {
    datasets.push({
      label: 'Download',
      data: [...props.downloadHistory],
      borderColor: getCssVar('--chart-dl-line') || '#7C3AED',
      backgroundColor: getCssVar('--chart-dl-fill') || 'rgba(124, 58, 237, 0.12)',
      borderWidth: 1.5,
      pointRadius: 0,
      pointHoverRadius: 3,
      fill: true,
      tension: 0.4,
    });
  }

  if (props.showUpload) {
    datasets.push({
      label: 'Upload',
      data: [...props.uploadHistory],
      borderColor: getCssVar('--chart-ul-line') || '#8b5cf6',
      backgroundColor: getCssVar('--chart-ul-fill') || 'rgba(139, 92, 246, 0.08)',
      borderWidth: 1.5,
      pointRadius: 0,
      pointHoverRadius: 3,
      fill: true,
      tension: 0.4,
    });
  }

  return { labels, datasets };
});

const chartOptions = computed((): ChartOptions<'line'> => ({
  responsive: true,
  maintainAspectRatio: false,
  animation: false,
  interaction: {
    mode: 'index',
    intersect: false,
  },
  plugins: {
    legend: {
      display: false,
    },
    tooltip: {
      backgroundColor: getCssVar('--chart-tooltip-bg') || '#1a2744',
      borderColor: getCssVar('--chart-tooltip-border') || '#2a2a4a',
      borderWidth: 1,
      titleColor: getCssVar('--chart-tick') || '#94a3b8',
      bodyColor: getCssVar('--text-primary') || '#ffffff',
      titleFont: { size: 11 },
      bodyFont: { size: 12 },
      callbacks: {
        label(ctx) {
          const label = ctx.dataset.label ?? '';
          const val = typeof ctx.parsed.y === 'number' ? ctx.parsed.y : 0;
          return `${label}: ${formatSpeed(val)}`;
        },
      },
    },
  },
  scales: {
    x: {
      display: false,
      grid: { display: false },
    },
    y: {
      display: true,
      position: 'right',
      min: 0,
      grid: {
        color: getCssVar('--chart-grid') || 'rgba(42, 42, 74, 0.6)',
      },
      border: {
        display: false,
      },
      ticks: {
        color: getCssVar('--chart-tick') || '#64748b',
        font: { size: 10 },
        maxTicksLimit: 4,
        callback(value: string | number) {
          return formatSpeed(Number(value));
        },
      },
    },
  },
}));
</script>

<template>
  <div class="speed-chart">
    <Line :data="chartData" :options="chartOptions" />
  </div>
</template>

<style scoped>
.speed-chart {
  position: relative;
  width: 100%;
  height: 120px;
}
</style>
