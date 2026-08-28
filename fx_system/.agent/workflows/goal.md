---
workflow: autonomous_forex_system
version: 2.0.0
target_platform: Ubuntu 24.04 LTS / cTrader Automate / Python 3.11+
---

# /goal: Autonomous EUR/USD Liquidity Sweep & MSS Trading System

## Objective
Build, verify, test, and deploy a distributed algorithmic execution and monitoring platform consisting of:
1. Low-latency native C# cTrader cBot running 15M Liquidity Sweeps + 1M Market Structure Shifts (MSS) + Fair Value Gap (FVG) execution.
2. Python FastAPI telemetry daemon logging tick metrics, trade states, and execution slip to PostgreSQL.
3. Asynchronous self-learning and Walk-Forward Optimization (WFA) pipeline updating trade parameters nightly.
4. Remote management Web Dashboard with live telemetry, strategy modifier controls, kill-switch, error logs, and ChatGPT macro analysis integration.

## Success Criteria
- [ ] cBot builds cleanly with ZeroMQ/IPC telemetry client.
- [ ] 100% test coverage on margin, pip-risk calculation, and FSM transitions.
- [ ] FastAPI backend handles concurrent status pushes from cBot and serves the remote dashboard.
- [ ] ChatGPT API integration operates strictly asynchronously as a trade-review and macro-advisory module (non-blocking for execution).
- [ ] Nightly self-learning skill validates parameters against historical tick data using Deflated Sharpe Ratio (DSR) before writing configs.
