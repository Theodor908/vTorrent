<script setup lang="ts">
import { computed, onMounted } from 'vue';
import { useProfileStore } from '@/stores/profileStore';
import { useToast } from '@/composables/useToast';
import * as api from '@/api/profiles';

const profileStore = useProfileStore();
const { showToast } = useToast();

const dayLabels = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'];
const hourLabels = Array.from({ length: 24 }, (_, i) => String(i).padStart(2, '0'));

const currentDayIndex = computed(() => {
  const day = new Date().getDay(); // 0=Sun
  return day === 0 ? 6 : day - 1;  // Remap to 0=Mon..6=Sun
});
const currentHour = computed(() => new Date().getHours());

onMounted(async () => {
  await profileStore.loadProfiles();
  await profileStore.loadActiveState();
  if (profileStore.scheduleEnabled) {
    await profileStore.loadSchedule();
  }
});

async function handleScheduleToggle(): Promise<void> {
  const newValue = !profileStore.scheduleEnabled;
  await profileStore.toggleSchedule(newValue);
  if (newValue) {
    await profileStore.loadSchedule();
  }
}

async function handleActivate(name: string): Promise<void> {
  try {
    await profileStore.activateProfile(name);
  } catch {
    // 409 if schedule active
  }
}

async function handleExport(): Promise<void> {
  try {
    await api.exportSchedule();
  } catch {
    showToast('Failed to export schedule.', 'error');
  }
}

async function handleImportFile(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement;
  const file = input.files?.[0];
  if (!file) return;

  try {
    const result = await api.importSchedule(file);
    if (result.success) {
      await profileStore.loadProfiles();
      await profileStore.loadSchedule();
      showToast('Schedule imported successfully.', 'success');
    } else {
      showToast('Schedule import completed with issues.', 'warning');
    }
  } catch {
    showToast('Failed to import schedule.', 'error');
  }
  input.value = '';
}
</script>

<template>
  <div class="profiles-schedule-tab">
    <!-- Schedule Toggle -->
    <section class="pst-section">
      <h3 class="pst-section__title">Schedule</h3>
      <div class="pst-toggle-row">
        <div>
          <span class="pst-toggle-row__label">Enable Schedule</span>
          <p class="pst-toggle-row__desc">
            {{ profileStore.scheduleEnabled
              ? 'Schedule is controlling profile changes automatically.'
              : 'Manual profile selection is active.' }}
          </p>
        </div>
        <button
          class="pst-toggle-btn"
          :class="{ 'pst-toggle-btn--on': profileStore.scheduleEnabled }"
          @click="handleScheduleToggle"
        >
          {{ profileStore.scheduleEnabled ? 'ON' : 'OFF' }}
        </button>
      </div>
    </section>

    <!-- Profile List -->
    <section class="pst-section">
      <h3 class="pst-section__title">Profiles</h3>
      <div class="pst-profile-list">
        <div
          v-for="profile in profileStore.profiles"
          :key="profile.name"
          class="pst-profile-card"
          :class="{ 'pst-profile-card--active': profile.name === profileStore.activeProfileName }"
        >
          <div class="pst-profile-card__info">
            <span class="pst-profile-card__dot" :style="{ background: profile.color }" />
            <span class="pst-profile-card__name">{{ profile.name }}</span>
            <span v-if="profile.name === profileStore.activeProfileName" class="pst-profile-card__badge">Active</span>
          </div>
          <button
            v-if="!profileStore.scheduleEnabled"
            class="pst-profile-card__btn"
            :disabled="profile.name === profileStore.activeProfileName"
            @click="handleActivate(profile.name)"
          >
            Activate
          </button>
        </div>
      </div>
      <p v-if="profileStore.scheduleEnabled" class="pst-hint">
        Profile switching is disabled while the schedule is active.
      </p>
    </section>

    <!-- Read-Only Schedule Grid -->
    <section v-if="profileStore.scheduleEnabled && profileStore.scheduleGrid" class="pst-section">
      <h3 class="pst-section__title">Weekly Schedule</h3>
      <div class="pst-schedule-actions">
        <button class="pst-action-btn" @click="handleExport">Export Schedule</button>
        <label class="pst-action-btn pst-action-btn--upload">
          Import Schedule
          <input type="file" accept=".json,.vtschedule.json" style="display: none" @change="handleImportFile" />
        </label>
      </div>
      <div class="pst-grid-wrapper">
        <div class="pst-grid">
          <!-- Hour labels row -->
          <div class="pst-grid__corner" />
          <div v-for="h in hourLabels" :key="'h'+h" class="pst-grid__hour-label">{{ h }}</div>
          <!-- Day rows -->
          <template v-for="(dayLabel, dayIdx) in dayLabels" :key="dayLabel">
            <div class="pst-grid__day-label">{{ dayLabel }}</div>
            <div
              v-for="(cell, hourIdx) in profileStore.scheduleGrid[dayIdx]"
              :key="`${dayIdx}-${hourIdx}`"
              class="pst-grid__cell"
              :class="{ 'pst-grid__cell--current': dayIdx === currentDayIndex && hourIdx === currentHour }"
              :style="{ background: cell.color }"
              :title="`${dayLabel} ${hourLabels[hourIdx]}:00 — ${cell.mode === 'Profile' ? cell.profileName : cell.mode}`"
            />
          </template>
        </div>
      </div>

      <!-- Legend -->
      <div class="pst-legend">
        <div v-for="profile in profileStore.profiles" :key="'l'+profile.name" class="pst-legend__item">
          <span class="pst-legend__dot" :style="{ background: profile.color }" />
          <span>{{ profile.name }}</span>
        </div>
        <div class="pst-legend__item">
          <span class="pst-legend__dot" style="background: #FFC107" />
          <span>Seed Only</span>
        </div>
        <div class="pst-legend__item">
          <span class="pst-legend__dot" style="background: #3C3C3C" />
          <span>Paused</span>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.profiles-schedule-tab { max-width: 900px; }

.pst-section { margin-bottom: 2rem; }
.pst-section__title { font-size: 1rem; font-weight: 700; margin-bottom: 1rem; color: var(--text-primary, #e0e0e0); }

.pst-toggle-row {
  display: flex; justify-content: space-between; align-items: center;
  padding: 1rem; border: 1px solid var(--border, #333); border-radius: 8px;
  background: var(--bg-secondary, #1a1a2e);
}
.pst-toggle-row__label { font-weight: 600; }
.pst-toggle-row__desc { font-size: 0.8rem; color: var(--text-secondary, #999); margin-top: 0.25rem; }

.pst-toggle-btn {
  padding: 0.35rem 1rem; border-radius: 6px; font-weight: 700; font-size: 0.8rem;
  border: 1px solid var(--border, #444); background: transparent; color: var(--text-primary, #e0e0e0); cursor: pointer;
}
.pst-toggle-btn--on { background: var(--accent-color, #4fc3f7); color: #000; border-color: transparent; }

.pst-profile-list { display: flex; flex-direction: column; gap: 0.5rem; }

.pst-profile-card {
  display: flex; justify-content: space-between; align-items: center;
  padding: 0.75rem 1rem; border: 1px solid var(--border, #333); border-radius: 8px;
  background: var(--bg-secondary, #1a1a2e);
}
.pst-profile-card--active { border-color: var(--accent-color, #4fc3f7); }

.pst-profile-card__info { display: flex; align-items: center; gap: 0.5rem; }
.pst-profile-card__dot { width: 10px; height: 10px; border-radius: 50%; flex-shrink: 0; }
.pst-profile-card__name { font-weight: 600; }
.pst-profile-card__badge {
  font-size: 0.65rem; padding: 0.1rem 0.45rem; border-radius: 12px;
  background: var(--accent-color, #4fc3f7); color: #000; font-weight: 700;
}

.pst-profile-card__btn {
  padding: 0.3rem 0.75rem; border-radius: 6px; font-size: 0.8rem;
  border: 1px solid var(--border, #444); background: transparent;
  color: var(--text-primary, #e0e0e0); cursor: pointer;
}
.pst-profile-card__btn:disabled { opacity: 0.4; cursor: not-allowed; }

.pst-hint { font-size: 0.8rem; color: var(--text-secondary, #999); margin-top: 0.5rem; font-style: italic; }

.pst-schedule-actions {
  display: flex;
  gap: 0.75rem;
  margin-bottom: 1rem;
}
.pst-action-btn {
  padding: 0.4rem 1rem;
  border-radius: 6px;
  font-size: 0.8rem;
  font-weight: 600;
  border: 1px solid var(--border, #444);
  background: transparent;
  color: var(--text-primary, #e0e0e0);
  cursor: pointer;
}
.pst-action-btn:hover {
  background: rgba(255, 255, 255, 0.08);
}
.pst-action-btn--upload {
  cursor: pointer;
}

/* Schedule Grid */
.pst-grid-wrapper { overflow-x: auto; }

.pst-grid {
  display: grid;
  grid-template-columns: 40px repeat(24, 1fr);
  gap: 1px;
  min-width: 600px;
}

.pst-grid__corner { background: transparent; }
.pst-grid__hour-label { font-size: 0.55rem; text-align: center; color: var(--text-secondary, #999); padding: 2px 0; }
.pst-grid__day-label { font-size: 0.7rem; font-weight: 600; display: flex; align-items: center; color: var(--text-secondary, #999); }

.pst-grid__cell {
  height: 20px; border-radius: 2px; min-width: 0;
}
.pst-grid__cell--current {
  outline: 2px solid #fff;
  outline-offset: -1px;
  z-index: 1;
}

/* Legend */
.pst-legend { display: flex; flex-wrap: wrap; gap: 1rem; margin-top: 0.75rem; }
.pst-legend__item { display: flex; align-items: center; gap: 0.35rem; font-size: 0.75rem; color: var(--text-secondary, #999); }
.pst-legend__dot { width: 10px; height: 10px; border-radius: 3px; flex-shrink: 0; }
</style>
