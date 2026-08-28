---
name: self_learning_wfa
description: Asynchronous Walk-Forward Analysis (WFA) and self-learning parameter tuning engine for the Forex execution bot.
---

# Walk-Forward Optimization & Self-Learning Engine

## 1. Purpose & Responsibilities
This skill governs the nightly and asynchronous self-learning pipeline that validates and optimizes strategy parameters against historical and recent execution data.

## 2. Walk-Forward Analysis Protocol
- **In-Sample / Out-of-Sample Split:** 70% in-sample training window, 30% out-of-sample forward test window.
- **Deflated Sharpe Ratio (DSR):** Parameters must pass DSR > 1.2 and Maximum Drawdown < 18% during out-of-sample testing.
- **Hot-Reload Validation:** Optimized parameters are written to `strategy_config.json` only after validation tests pass.

## 3. Tunable Strategy Parameters
- `displacement_atr_mult` (range: 1.2 - 2.5)
- `risk_reward_ratio` (range: 2.0 - 5.0)
- `invalidation_buffer_pips` (range: 0.5 - 3.0)
- `max_pending_bars` (range: 4 - 12)

## 4. Execution Directives
- Optimization must run out-of-band and never block the fast execution plane.
- Emits verification telemetry to backend upon completion.
