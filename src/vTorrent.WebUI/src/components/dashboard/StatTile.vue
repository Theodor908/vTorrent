<script setup lang="ts">
defineProps<{
  title: string;
  headline: string;
  headlineLabel: string;
  items: Array<{ label: string; value: string | number; color?: string }>;
}>();
</script>

<template>
  <div class="stat-tile">
    <h4 class="stat-tile__title">{{ title }}</h4>
    <div class="stat-tile__headline">
      <span class="stat-tile__number">{{ headline }}</span>
      <span class="stat-tile__label">{{ headlineLabel }}</span>
    </div>
    <div class="stat-tile__items">
      <div v-for="item in items" :key="item.label" class="stat-tile__item">
        <span class="stat-tile__item-label">{{ item.label }}</span>
        <span class="stat-tile__item-value" :style="item.color ? { color: item.color } : {}">{{ item.value }}</span>
      </div>
    </div>
  </div>
</template>

<style scoped>
.stat-tile {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  padding: var(--spacing-md);
  display: flex;
  flex-direction: column;
  gap: var(--spacing-sm);
}

.stat-tile__title {
  font-size: var(--font-xs);
  font-weight: 700;
  color: var(--text-secondary);
  letter-spacing: 0.1em;
}

.stat-tile__headline {
  display: flex;
  align-items: baseline;
  gap: var(--spacing-sm);
}

.stat-tile__number {
  font-size: var(--font-xl);
  font-weight: 700;
  color: var(--accent-active);
  font-variant-numeric: tabular-nums;
}

.stat-tile__label {
  font-size: var(--font-xs);
  color: var(--text-tertiary);
}

.stat-tile__items {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 4px var(--spacing-md);
  font-size: var(--font-xs);
}

.stat-tile__item {
  display: flex;
  justify-content: space-between;
  gap: var(--spacing-xs);
}

.stat-tile__item-label {
  color: var(--text-tertiary);
}

.stat-tile__item-value {
  color: var(--text-primary);
  font-weight: 500;
  font-variant-numeric: tabular-nums;
}
</style>
