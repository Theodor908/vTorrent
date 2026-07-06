<script setup lang="ts">
import { ref, computed, watch, onMounted } from 'vue';
import { useRouter } from 'vue-router';
import { useAuth } from '@/composables/useAuth';
import { useConnection } from '@/composables/useConnection';

// ============================================================
// Composables
// ============================================================

const router = useRouter();
const auth = useAuth();
const connection = useConnection();

// ============================================================
// Profile state
// ============================================================

const selectedProfileId = ref(connection.activeProfile.value.id);

const selectedProfile = computed(() =>
  connection.profiles.value.find((p) => p.id === selectedProfileId.value)
);

watch(selectedProfileId, (id) => {
  const profile = connection.profiles.value.find((p) => p.id === id);
  if (profile?.username) {
    username.value = profile.username;
  }
});

// ============================================================
// Form state
// ============================================================

const username = ref(connection.activeProfile.value.username ?? '');
const password = ref('');
const loading = ref(false);
const errorMessage = ref('');

// ============================================================
// Lifecycle
// ============================================================

onMounted(async () => {
  // Check local-access bypass and attempt silent token refresh.
  const authenticated = await auth.initialize();
  if (authenticated) {
    await router.push('/');
  }
});

// ============================================================
// Handlers
// ============================================================

function clearError(): void {
  errorMessage.value = '';
}

async function handleSubmit(): Promise<void> {
  if (selectedProfileId.value !== connection.activeProfile.value.id) {
    connection.switchProfile(selectedProfileId.value);
    return; // Page will reload
  }

  if (!username.value.trim() || !password.value) {
    errorMessage.value = 'Username and password are required.';
    return;
  }

  loading.value = true;
  errorMessage.value = '';

  try {
    await auth.login({ username: username.value.trim(), password: password.value });
    await router.push('/');
  } catch (err: unknown) {
    // Attempt to extract server error message
    const apiError = err as { response?: { data?: { message?: string } }; message?: string };
    errorMessage.value =
      apiError?.response?.data?.message ??
      apiError?.message ??
      'Login failed. Please check your credentials.';
  } finally {
    loading.value = false;
  }
}
</script>

<template>
  <div class="login-page">
    <!-- Subtle background grid overlay -->
    <div class="login-page__bg" aria-hidden="true" />

    <div class="login-card" role="main">
      <!-- Logo -->
      <div class="login-card__logo" aria-label="vTorrent">
        <span class="login-card__logo-v" aria-hidden="true">V</span>
        <span class="login-card__logo-text">TORRENT</span>
      </div>

      <p class="login-card__subtitle">Sign in to continue</p>

      <!-- Login form -->
      <form class="login-form" @submit.prevent="handleSubmit" novalidate>
        <!-- Server profile selector (only shown when 2+ profiles exist) -->
        <div class="login-form__field" v-if="connection.profiles.value.length > 1">
          <label class="login-form__label" for="server-profile">Server</label>
          <select
            id="server-profile"
            v-model="selectedProfileId"
            class="login-form__input login-form__select"
            :disabled="loading"
          >
            <option
              v-for="profile in connection.profiles.value"
              :key="profile.id"
              :value="profile.id"
            >
              {{ profile.name }}{{ profile.host ? ` — ${profile.host}` : '' }}
            </option>
          </select>
        </div>

        <!-- Username -->
        <div class="login-form__field">
          <label class="login-form__label" for="login-username">Username</label>
          <input
            id="login-username"
            v-model="username"
            class="login-form__input"
            type="text"
            placeholder="Enter username"
            autocomplete="username"
            autocapitalize="off"
            spellcheck="false"
            :disabled="loading"
            @input="clearError"
            @keydown.enter.prevent="handleSubmit"
          />
        </div>

        <!-- Password -->
        <div class="login-form__field">
          <label class="login-form__label" for="login-password">Password</label>
          <input
            id="login-password"
            v-model="password"
            class="login-form__input"
            type="password"
            placeholder="Enter password"
            autocomplete="current-password"
            :disabled="loading"
            @input="clearError"
            @keydown.enter.prevent="handleSubmit"
          />
        </div>

        <!-- Error message -->
        <div
          v-if="errorMessage"
          class="login-form__error"
          role="alert"
          aria-live="polite"
        >
          <span class="login-form__error-icon" aria-hidden="true">!</span>
          {{ errorMessage }}
        </div>

        <!-- Submit -->
        <button
          class="login-form__submit"
          type="submit"
          :disabled="loading"
          :aria-busy="loading"
        >
          <span v-if="loading" class="login-form__spinner" aria-hidden="true" />
          <span>{{ loading ? 'Signing in…' : 'Sign In' }}</span>
        </button>

        <!-- Manage profiles link -->
        <div class="login-form__manage-profiles">
          <router-link to="/settings?tab=profiles" class="login-form__profiles-link">
            Manage Server Profiles
          </router-link>
        </div>
      </form>
    </div>
  </div>
</template>

<style scoped>
/* ── Page shell ────────────────────────────────────────────── */
.login-page {
  position: relative;
  display: flex;
  align-items: center;
  justify-content: center;
  min-height: 100vh;
  background: var(--bg-primary);
  overflow: hidden;
}

/* Subtle dot-grid background */
.login-page__bg {
  position: absolute;
  inset: 0;
  background-image:
    radial-gradient(
      ellipse 80% 60% at 50% -10%,
      var(--login-glow-1) 0%,
      transparent 70%
    ),
    radial-gradient(
      ellipse 60% 40% at 80% 110%,
      var(--login-glow-2) 0%,
      transparent 70%
    ),
    radial-gradient(
      circle at 1px 1px,
      color-mix(in srgb, var(--text-primary) 4%, transparent) 1px,
      transparent 0
    );
  background-size: 100% 100%, 100% 100%, 24px 24px;
  pointer-events: none;
  z-index: 0;
}

/* ── Card ──────────────────────────────────────────────────── */
.login-card {
  position: relative;
  z-index: 1;
  width: 100%;
  max-width: 380px;
  margin: var(--spacing-xl);
  padding: var(--spacing-2xl);
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  box-shadow:
    0 0 0 1px color-mix(in srgb, var(--accent-active) 4%, transparent),
    0 20px 60px rgba(0, 0, 0, 0.5),
    0 8px 24px rgba(0, 0, 0, 0.3);
}

/* ── Logo ──────────────────────────────────────────────────── */
.login-card__logo {
  display: flex;
  align-items: baseline;
  justify-content: center;
  gap: 5px;
  margin-bottom: var(--spacing-sm);
  user-select: none;
}

.login-card__logo-v {
  font-size: 48px;
  font-weight: 900;
  color: var(--accent-active);
  letter-spacing: -1px;
  line-height: 1;
  /* Subtle glow matching the accent color */
  filter: drop-shadow(0 0 12px color-mix(in srgb, var(--accent-active) 40%, transparent));
}

.login-card__logo-text {
  font-size: 22px;
  font-weight: 800;
  color: var(--text-primary);
  letter-spacing: 0.25em;
  line-height: 1;
}

.login-card__subtitle {
  text-align: center;
  color: var(--text-tertiary);
  font-size: var(--font-sm);
  font-weight: 400;
  margin-bottom: var(--spacing-2xl);
  letter-spacing: 0.02em;
}

/* ── Form ──────────────────────────────────────────────────── */
.login-form {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-lg);
}

.login-form__field {
  display: flex;
  flex-direction: column;
  gap: var(--spacing-xs);
}

.login-form__label {
  font-size: var(--font-sm);
  font-weight: 600;
  color: var(--text-secondary);
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.login-form__input {
  width: 100%;
  height: 40px;
  padding: 0 var(--spacing-md);
  background: var(--bg-input);
  border: 1px solid var(--border);
  border-radius: var(--radius-md);
  color: var(--text-primary);
  font-size: var(--font-md);
  outline: none;
  transition:
    border-color var(--transition-fast),
    box-shadow var(--transition-fast);
}

.login-form__input:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.login-form__input:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.login-form__input::placeholder {
  color: var(--text-tertiary);
}

/* ── Select (profile dropdown) ─────────────────────────────── */
.login-form__select {
  appearance: none;
  -webkit-appearance: none;
  cursor: pointer;
  background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='8' viewBox='0 0 12 8'%3E%3Cpath d='M1 1l5 5 5-5' stroke='%236b7280' stroke-width='1.5' fill='none' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E");
  background-repeat: no-repeat;
  background-position: right var(--spacing-md) center;
  padding-right: calc(var(--spacing-md) * 2 + 12px);
}

.login-form__select:focus {
  border-color: var(--border-focus);
  box-shadow: 0 0 0 2px color-mix(in srgb, var(--accent-active) 12%, transparent);
}

.login-form__select option {
  background: var(--bg-secondary);
  color: var(--text-primary);
}

/* ── Manage profiles link ──────────────────────────────────── */
.login-form__manage-profiles {
  display: flex;
  justify-content: center;
  margin-top: var(--spacing-xs);
}

.login-form__profiles-link {
  font-size: var(--font-sm);
  color: var(--text-tertiary);
  text-decoration: none;
  letter-spacing: 0.02em;
  transition: color var(--transition-fast);
}

.login-form__profiles-link:hover {
  color: var(--accent-active);
  text-decoration: underline;
}

/* ── Error ─────────────────────────────────────────────────── */
.login-form__error {
  display: flex;
  align-items: center;
  gap: var(--spacing-sm);
  padding: var(--spacing-sm) var(--spacing-md);
  background: rgba(239, 68, 68, 0.08);
  border: 1px solid rgba(239, 68, 68, 0.3);
  border-radius: var(--radius-md);
  color: var(--status-red);
  font-size: var(--font-sm);
  line-height: 1.4;
  animation: shake 0.3s ease;
}

.login-form__error-icon {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  background: var(--status-red);
  color: #fff;
  font-size: 11px;
  font-weight: 900;
  flex-shrink: 0;
  line-height: 1;
}

/* ── Submit button ─────────────────────────────────────────── */
.login-form__submit {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: var(--spacing-sm);
  width: 100%;
  height: 42px;
  margin-top: var(--spacing-sm);
  padding: 0 var(--spacing-xl);
  background: var(--accent-cyan);
  color: var(--bg-primary);
  border: none;
  border-radius: var(--radius-md);
  font-size: var(--font-md);
  font-weight: 700;
  letter-spacing: 0.04em;
  cursor: pointer;
  transition:
    background-color var(--transition-fast),
    box-shadow var(--transition-fast),
    opacity var(--transition-fast),
    transform var(--transition-fast);
  box-shadow: 0 2px 12px color-mix(in srgb, var(--accent-active) 25%, transparent);
}

.login-form__submit:hover:not(:disabled) {
  filter: brightness(1.1);
  box-shadow: 0 4px 20px color-mix(in srgb, var(--accent-active) 40%, transparent);
  transform: translateY(-1px);
}

.login-form__submit:active:not(:disabled) {
  transform: translateY(0);
  box-shadow: 0 2px 8px color-mix(in srgb, var(--accent-active) 20%, transparent);
}

.login-form__submit:disabled {
  opacity: 0.6;
  cursor: not-allowed;
  transform: none;
}

/* ── Spinner ───────────────────────────────────────────────── */
.login-form__spinner {
  display: inline-block;
  width: 14px;
  height: 14px;
  border: 2px solid rgba(10, 10, 26, 0.3);
  border-top-color: var(--bg-primary);
  border-radius: 50%;
  animation: spin 0.7s linear infinite;
  flex-shrink: 0;
}

/* ── Animations ────────────────────────────────────────────── */
@keyframes spin {
  to { transform: rotate(360deg); }
}

@keyframes shake {
  0%, 100% { transform: translateX(0); }
  20%       { transform: translateX(-4px); }
  40%       { transform: translateX(4px); }
  60%       { transform: translateX(-3px); }
  80%       { transform: translateX(3px); }
}

/* ── Responsive ────────────────────────────────────────────── */
@media (max-width: 480px) {
  .login-card {
    margin: var(--spacing-md);
    padding: var(--spacing-xl);
  }

  .login-card__logo-v {
    font-size: 40px;
  }

  .login-card__logo-text {
    font-size: 18px;
  }
}
</style>
