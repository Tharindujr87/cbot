---
priority: 0
scope: security_and_fault_tolerance
---

# Error Checking, Fault Tolerance & Security Directives

## 1. Margin & Execution Safety Rules
- **Free Margin Buffer:** Never commit more than 85% of available free margin to avoid instant margin stop-out on 1:30 leverage accounts.
- **Max Account Drawdown:** Hard halt at 30% daily loss. All pending orders are purged immediately.
- **Spread Guard:** If spread exceeds 1.2 pips at entry, abort limit order placement.

## 2. Remote WebApp Security
- **Authentication:** All control endpoints (`/api/control/*`) require JWT tokens signed with RS256 or HS256, plus API key header validation.
- **CORS & Binding:** Web application daemon binds strictly to `127.0.0.1` locally; expose publicly ONLY via an encrypted NGINX reverse proxy with SSL (Let's Encrypt) and basic HTTP auth or VPN/Tailscale.
- **Kill-Switch Idempotency:** The `/api/control/kill` endpoint must immediately issue a cancel-all and flatten-all command across the IPC socket and lock the state machine.