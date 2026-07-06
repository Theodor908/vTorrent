<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue';

const props = defineProps<{
  visible: boolean;
  position: { x: number; y: number };
  items: Array<{ label: string; action: string; danger?: boolean }>;
}>();

const emit = defineEmits<{
  (e: 'close'): void;
  (e: 'action', action: string): void;
}>();

const menuRef = ref<HTMLElement | null>(null);

function handleClick(action: string): void {
  emit('action', action);
  emit('close');
}

function handleOutside(event: MouseEvent): void {
  if (menuRef.value && !menuRef.value.contains(event.target as Node)) {
    emit('close');
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') emit('close');
}

onMounted(() => {
  document.addEventListener('mousedown', handleOutside, true);
  document.addEventListener('keydown', handleKeydown);
});

onUnmounted(() => {
  document.removeEventListener('mousedown', handleOutside, true);
  document.removeEventListener('keydown', handleKeydown);
});
</script>

<template>
  <Teleport to="body">
    <div
      v-if="visible"
      ref="menuRef"
      class="ctx-menu"
      :style="{ left: `${position.x}px`, top: `${position.y}px` }"
      role="menu"
    >
      <button
        v-for="item in items"
        :key="item.action"
        class="ctx-menu__item"
        :class="{ 'ctx-menu__item--danger': item.danger }"
        role="menuitem"
        @click="handleClick(item.action)"
      >
        {{ item.label }}
      </button>
    </div>
  </Teleport>
</template>

<style scoped>
.ctx-menu {
  position: fixed;
  z-index: 9999;
  min-width: 160px;
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  box-shadow: var(--shadow-lg);
  padding: var(--spacing-xs) 0;
}

.ctx-menu__item {
  display: block;
  width: 100%;
  padding: 6px var(--spacing-lg);
  text-align: left;
  font-size: var(--font-sm);
  color: var(--text-primary);
  cursor: pointer;
}

.ctx-menu__item:hover {
  background: var(--bg-hover);
}

.ctx-menu__item--danger {
  color: var(--status-red);
}
</style>
