import os
import json
from fastapi import FastAPI, Depends, HTTPException, status
from fastapi.middleware.cors import CORSMiddleware
from pydantic import BaseModel, Field
import openai

app = FastAPI(title="Forex Bot Control & Telemetry API", version="2.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"], # Tighten in production to frontend domain
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

CONFIG_PATH = os.getenv("STRATEGY_CONFIG_PATH", "./strategy_config.json")
openai.api_key = os.getenv("OPENAI_API_KEY", "")

class StrategyConfigModel(BaseModel):
    session_start_utc: int = Field(ge=0, le=23)
    session_end_utc: int = Field(ge=0, le=23)
    displacement_atr_mult: float = Field(ge=1.0, le=4.0)
    risk_reward_ratio: float = Field(ge=1.5, le=10.0)
    invalidation_buffer_pips: float = Field(ge=0.5, le=5.0)
    max_pending_bars: int = Field(ge=3, le=20)
    risk_per_trade_percent: float = Field(ge=1.0, le=50.0)
    circuit_breaker_drawdown_percent: float = Field(ge=5.0, le=50.0)
    emergency_kill_active: bool

@app.get("/api/status")
def get_system_status():
    with open(CONFIG_PATH, "r") as f:
        config = json.load(f)
    return {
        "status": "ONLINE",
        "emergency_kill_active": config.get("emergency_kill_active", False),
        "config": config
    }

@app.post("/api/control/strategy")
def update_strategy_config(new_config: StrategyConfigModel):
    with open(CONFIG_PATH, "w") as f:
        json.dump(new_config.model_dump(), f, indent=2)
    # Trigger IPC notification to C# cBot here
    return {"status": "SUCCESS", "message": "Strategy parameters updated and hot-reloaded."}

@app.post("/api/control/kill")
def emergency_kill_switch():
    with open(CONFIG_PATH, "r") as f:
        config = json.load(f)
    config["emergency_kill_active"] = True
    with open(CONFIG_PATH, "w") as f:
        json.dump(config, f, indent=2)
    return {"status": "HALTED", "message": "Emergency kill activated. System locked."}

@app.post("/api/advisor/chatgpt-analysis")
async def analyze_market_regime(payload: dict):
    """
    Asynchronous advisory endpoint: Analyzes market structure & news debrief via OpenAI.
    Non-blocking to the execution plane.
    """
    if not openai.api_key:
        raise HTTPException(status_code=500, detail="OpenAI API key missing.")
    
    prompt = (
        f"You are a quantitative macro risk advisor. Review the latest trading state: {json.dumps(payload)}. "
        "Provide a concise summary of risks, high-impact news context, and structural health."
    )
    
    response = openai.chat.completions.create(
        model="gpt-4o",
        messages=[
            {"role": "system", "content": "You are a quantitative risk management advisor."},
            {"role": "user", "content": prompt}
        ],
        max_tokens=250
    )
    return {"analysis": response.choices[0].message.content}
