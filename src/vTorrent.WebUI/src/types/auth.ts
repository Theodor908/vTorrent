// auth.ts — TypeScript types mirroring C# auth DTOs from vTorrent.Server
// All types match what the REST API returns as camelCase JSON.

// ============================================================
// LoginRequest — POST /auth/login
// Maps to vTorrent.Server.Models.LoginRequest
// ============================================================

export interface LoginRequest {
  username: string;
  password: string;
}

// ============================================================
// LoginResponse / TokenResponse — response from POST /auth/login and POST /auth/refresh
// Maps to vTorrent.Server.Models.TokenResponse
// ============================================================

export interface LoginResponse {
  accessToken: string;
  refreshToken: string;
  /** Access token lifetime in seconds */
  expiresIn: number;
}

// ============================================================
// RefreshRequest — POST /auth/refresh and POST /auth/logout
// Maps to vTorrent.Server.Models.RefreshRequest
// ============================================================

export interface RefreshRequest {
  refreshToken: string;
}

// ============================================================
// ChangePasswordRequest — POST /auth/change-password
// Maps to vTorrent.Server.Models.ChangePasswordRequest
// ============================================================

export interface ChangePasswordRequest {
  currentPassword: string;
  newPassword: string;
}

// ============================================================
// Error response shape — returned by the server on failure
// Maps to vTorrent.Server.Models.ErrorResponse
// ============================================================

export interface ErrorResponse {
  message: string;
  code: string;
}

// ============================================================
// CreateApiKeyRequest — POST /auth/api-keys
// Maps to vTorrent.Server.Models.CreateApiKeyRequest
// ============================================================

export interface CreateApiKeyRequest {
  label: string
}

// ============================================================
// CreateApiKeyResponse — response from POST /auth/api-keys
// Maps to vTorrent.Server.Models.CreateApiKeyResponse
// ============================================================

export interface CreateApiKeyResponse {
  apiKey: string
  keyPrefix: string
  label: string
  createdAt: number
}

// ============================================================
// ApiKeyListItem — GET /auth/api-keys
// Maps to vTorrent.Server.Models.ApiKeyListItem
// ============================================================

export interface ApiKeyListItem {
  keyPrefix: string
  label: string
  createdAt: number
  lastUsed: number | null
  isRevoked: boolean
}
