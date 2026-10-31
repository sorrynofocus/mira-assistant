import { ApiError, postJson, refreshCsrfToken, setCsrfToken } from './api';

export type SignOutResult = { signedOut: true } | { signedOut: false; message: string };

export const SIGN_OUT_FAILED = 'Sign-out failed, so you are still signed in. Check that MIRA is running, then try again.';

// The sign-in screen may only appear once the server has revoked the session, or reports it already ended (401).
// Any other failure leaves the session cookie valid, so the caller must keep the user signed in and say so.
export async function signOut(): Promise<SignOutResult> {
  for (let attempt = 1; ; attempt++) {
    try {
      const result = await postJson<{ csrfToken: string }>('/api/auth/logout', {});
      setCsrfToken(result.csrfToken);
      return { signedOut: true };
    } catch (error) {
      if (error instanceof ApiError && error.status === 401) {
        await refreshCsrfToken().catch(() => undefined);
        return { signedOut: true };
      }
      // A stale CSRF token is refused with 403: fetch a fresh one and retry once.
      if (error instanceof ApiError && error.status === 403 && attempt === 1) {
        try {
          await refreshCsrfToken();
          continue;
        } catch {
          // Fall through to the failure result.
        }
      }
      return { signedOut: false, message: SIGN_OUT_FAILED };
    }
  }
}
