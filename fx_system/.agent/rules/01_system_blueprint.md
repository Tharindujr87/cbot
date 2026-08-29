---
priority: 1
scope: architectural_standard
---

# Master System Blueprint & Component Topography

## 1. Dual-Plane Topology
- **Fast Execution Plane (cTrader Automate / C#):**
  - Executes directly on broker tick events with deterministic rules.
  - No external synchronous HTTP/LLM calls in the order routing loop.
  - Emits event streams via local Unix Domain Socket or ZeroMQ (IPC).
- **Cognitive & Remote Management Plane (FastAPI / PostgreSQL / React):**
  - Consumes execution events, updates account telemetry, provides REST/WebSocket endpoints.
  - Interacts with OpenAI API for macro sentiment and daily trade debriefs.
  - Serves mobile-responsive remote control dashboard.

## 2. Dynamic Parameter IPC Contract
The execution cBot reads its operational bounds from a shared hot-reloaded memory state / config `strategy_config.json`:
```json
{
  "session_start_utc": 12,
  "session_end_utc": 17,
  "displacement_atr_mult": 1.8,
  "risk_reward_ratio": 3.5,
  "invalidation_buffer_pips": 1.5,
  "max_pending_bars": 8,
  "risk_per_trade_percent": 15.0,
  "circuit_breaker_drawdown_percent": 30.0,
  "emergency_kill_active": false
}