import os
import json
from fastapi import APIRouter, HTTPException, status
from ..models.schema import StrategyConfigModel, ControlActionRequest
from ..services.ipc_bridge import ipc_bridge_service

router = APIRouter(prefix="/api/control", tags=["Control & Strategy Management"])
CONFIG_PATH = os.getenv("STRATEGY_CONFIG_PATH", "strategy_config.json")

def load_config() -> dict:
    if os.path.exists(CONFIG_PATH):
        with open(CONFIG_PATH, "r") as f:
            return json.load(f)
    return {
        "session_start_utc": 12,
        "session_end_utc": 17,
        "displacement_atr_mult": 1.8,
        "risk_reward_ratio": 3.5,
        "invalidation_buffer_pips": 1.5,
        "max_pending_bars": 8,
        "risk_per_trade_percent": 15.0,
        "circuit_breaker_drawdown_percent": 30.0,
        "emergency_kill_active": False
    }

def save_config(cfg: dict):
    with open(CONFIG_PATH, "w") as f:
        json.dump(cfg, f, indent=2)

@router.get("/config", response_model=StrategyConfigModel)
def get_strategy_config():
    return load_config()

@router.post("/strategy")
def update_strategy_config(new_config: StrategyConfigModel):
    cfg_data = new_config.model_dump()
    save_config(cfg_data)
    
    # Notify execution bot via IPC
    ipc_res = ipc_bridge_service.send_command_to_cbot("HOT_RELOAD_CONFIG", cfg_data)
    
    return {
        "status": "SUCCESS",
        "message": "Strategy parameters updated and hot-reloaded to execution plane.",
        "ipc_response": ipc_res,
        "config": cfg_data
    }

@router.post("/kill")
def emergency_kill_switch():
    cfg = load_config()
    cfg["emergency_kill_active"] = True
    save_config(cfg)
    
    # Send instant kill & flatten order to execution engine
    ipc_res = ipc_bridge_service.send_command_to_cbot("EMERGENCY_KILL", {"flatten_positions": True, "cancel_pending": True})
    
    return {
        "status": "HALTED",
        "message": "EMERGENCY KILL SWITCH TRIGGERED: All pending orders purged, open trades flattened, state locked.",
        "ipc_response": ipc_res
    }

@router.post("/resume")
def resume_system_operation():
    cfg = load_config()
    cfg["emergency_kill_active"] = False
    save_config(cfg)
    
    ipc_res = ipc_bridge_service.send_command_to_cbot("RESUME_OPERATION", {})
    return {
        "status": "ONLINE",
        "message": "System lock removed. Fast execution plane returned to active standby.",
        "ipc_response": ipc_res
    }
