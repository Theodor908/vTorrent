<script setup lang="ts">
// ProgressBar.vue — Simple reusable progress bar for torrent completion display.
// Supports 'download' (cyan gradient) and 'seeding' (green gradient) variants.

withDefaults(
  defineProps<{
    value: number; // 0–1
    variant?: 'download' | 'seeding';
  }>(),
  {
    variant: 'download',
  },
);
</script>

<template>
  <div class="progress-track" role="progressbar" :aria-valuenow="Math.round(value * 100)" aria-valuemin="0" aria-valuemax="100">
    <div
      class="progress-fill"
      :class="`progress-fill--${variant}`"
      :style="{ width: `${Math.min(Math.max(value * 100, 0), 100)}%` }"
    />
  </div>
</template>

<style scoped>
.progress-track {
  width: 100%;
  height: 5px;
  background: var(--bg-input);
  border-radius: 2.5px;
  overflow: hidden;
}

.progress-fill {
  height: 100%;
  border-radius: 2.5px;
  transition: width 300ms ease;
  min-width: 0;
}

.progress-fill--download {
  background: linear-gradient(90deg, #00AEEF, #54D4F3);
}

.progress-fill--seeding {
  background: linear-gradient(90deg, var(--status-green), #34d399);
}
</style>
