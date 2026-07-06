<script setup lang="ts">
import { ref, watch } from 'vue';
import * as categoriesApi from '@/api/categories';
import type { Category } from '@/api/categories';
import { useToast } from '@/composables/useToast';

const props = defineProps<{
  visible: boolean;
  category?: Category | null;
}>();

const emit = defineEmits<{
  (e: 'close'): void;
  (e: 'saved'): void;
}>();

const { showToast } = useToast();
const name = ref('');
const savePath = ref('');
const saving = ref(false);

const isEdit = () => !!props.category;

watch(() => props.visible, (v) => {
  if (v) {
    name.value = props.category?.name ?? '';
    savePath.value = props.category?.savePath ?? '';
  }
});

async function handleSave(): Promise<void> {
  if (!name.value.trim()) return;
  saving.value = true;
  try {
    if (isEdit() && props.category) {
      await categoriesApi.updateCategory(props.category.id, {
        name: name.value.trim(),
        savePath: savePath.value.trim() || null,
      });
      showToast('Category updated.', 'success');
    } else {
      await categoriesApi.createCategory({
        name: name.value.trim(),
        savePath: savePath.value.trim() || null,
      });
      showToast('Category created.', 'success');
    }
    emit('saved');
    emit('close');
  } catch {
    showToast('Failed to save category.', 'error');
  } finally {
    saving.value = false;
  }
}

async function handleDelete(): Promise<void> {
  if (!props.category) return;
  saving.value = true;
  try {
    await categoriesApi.deleteCategory(props.category.id);
    showToast('Category deleted.', 'success');
    emit('saved');
    emit('close');
  } catch {
    showToast('Failed to delete category.', 'error');
  } finally {
    saving.value = false;
  }
}
</script>

<template>
  <Teleport to="body">
    <div v-if="visible" class="dialog-backdrop" @click.self="emit('close')">
      <div class="dialog" role="dialog" :aria-label="isEdit() ? 'Edit Category' : 'New Category'">
        <h3 class="dialog__title">{{ isEdit() ? 'Edit Category' : 'New Category' }}</h3>

        <div class="dialog__field">
          <label class="dialog__label" for="cat-name">Name</label>
          <input id="cat-name" v-model="name" class="dialog__input" type="text" placeholder="Category name" />
        </div>

        <div class="dialog__field">
          <label class="dialog__label" for="cat-path">Default Save Path</label>
          <input id="cat-path" v-model="savePath" class="dialog__input" type="text" placeholder="Leave empty for default" />
        </div>

        <div class="dialog__actions">
          <button v-if="isEdit()" class="dialog__btn dialog__btn--danger" :disabled="saving" @click="handleDelete">Delete</button>
          <div class="dialog__spacer" />
          <button class="dialog__btn dialog__btn--secondary" :disabled="saving" @click="emit('close')">Cancel</button>
          <button class="dialog__btn dialog__btn--primary" :disabled="saving || !name.trim()" @click="handleSave">
            {{ saving ? 'Saving\u2026' : 'Save' }}
          </button>
        </div>
      </div>
    </div>
  </Teleport>
</template>

<style scoped>
.dialog-backdrop {
  position: fixed;
  inset: 0;
  z-index: 10000;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
}
.dialog {
  background: var(--bg-card);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  padding: var(--spacing-xl);
  width: 100%;
  max-width: 400px;
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}
.dialog__title { font-size: var(--font-lg); font-weight: 700; color: var(--text-primary); }
.dialog__field { display: flex; flex-direction: column; gap: var(--spacing-xs); }
.dialog__label { font-size: var(--font-xs); font-weight: 600; color: var(--text-tertiary); text-transform: uppercase; letter-spacing: 0.04em; }
.dialog__input { height: 36px; padding: 0 var(--spacing-md); background: var(--bg-input); border: 1px solid var(--border); border-radius: var(--radius-md); color: var(--text-primary); font-size: var(--font-md); }
.dialog__input:focus { border-color: var(--border-focus); }
.dialog__actions { display: flex; align-items: center; gap: var(--spacing-sm); }
.dialog__spacer { flex: 1; }
.dialog__btn { padding: 6px var(--spacing-lg); border-radius: var(--radius-md); font-size: var(--font-sm); font-weight: 600; cursor: pointer; }
.dialog__btn:disabled { opacity: 0.5; cursor: not-allowed; }
.dialog__btn--primary { background: var(--accent-active); color: var(--bg-primary); }
.dialog__btn--secondary { background: var(--bg-hover); color: var(--text-primary); border: 1px solid var(--border); }
.dialog__btn--danger { background: transparent; color: var(--status-red); border: 1px solid var(--status-red); }
</style>
