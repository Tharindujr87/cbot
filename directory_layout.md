.agent/
├── manifest.json
├── rules/
│   ├── 01_system_blueprint.md
│   └── 02_security_guardrails.md
├── skills/
│   ├── self_learning_wfa/
│   │   └── SKILL.md
│   └── ctrader_fix_bridge/
│       └── SKILL.md
└── workflows/
    └── goal.md
backend/
├── app/
│   ├── main.py
│   ├── api/
│   │   ├── routes_control.py
│   │   ├── routes_telemetry.py
│   │   └── routes_llm.py
│   ├── services/
│   │   ├── ipc_bridge.py
│   │   ├── wfa_engine.py
│   │   └── chatgpt_advisor.py
│   └── models/
│       └── schema.py
└── database/
    └── init.sql
cbot/
└── LiquiditySweepMssBot.cs
frontend/
└── ... (React/Vue WebApp)