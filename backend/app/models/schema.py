from datetime import datetime
from typing import Optional, Dict, Any, List
from pydantic import BaseModel, Field

class StrategyConfigModel(BaseModel):
    session_start_utc: int = Field(default=12, ge=0, le=23, description="Trading session start hour (UTC)")
    session_end_utc: int = Field(default=17, ge=0, le=23, description="Trading session end hour (UTC)")
    displacement_atr_mult: float = Field(default=1.8, ge=1.0, le=4.0, description="ATR multiplier required for 1M candle displacement")
    risk_reward_ratio: float = Field(default=3.5, ge=1.5, le=10.0, description="Target Risk-to-Reward ratio")
    invalidation_buffer_pips: float = Field(default=1.5, ge=0.5, le=5.0, description="Buffer in pips above/below sweep level for SL")
    max_pending_bars: int = Field(default=8, ge=3, le=20, description="Max bars limit order can remain open before auto-cancelling")
    risk_per_trade_percent: float = Field(default=15.0, ge=1.0, le=50.0, description="Account balance risk per trade")
    circuit_breaker_drawdown_percent: float = Field(default=30.0, ge=5.0, le=50.0, description="Max daily drawdown circuit breaker")
    emergency_kill_active: bool = Field(default=False, description="Emergency kill status")

class TelemetryTickModel(BaseModel):
    symbol: str = "EURUSD"
    bid: float
    ask: float
    spread_pips: float
    fsm_state: str
    free_margin: Optional[float] = None
    margin_level_percent: Optional[float] = None
    account_equity: Optional[float] = None
    account_balance: Optional[float] = None
    daily_pnl: Optional[float] = 0.0
    daily_drawdown_percent: Optional[float] = 0.0
    timestamp_utc: Optional[str] = None

class TradeLogModel(BaseModel):
    ticket_id: str
    symbol: str
    trade_type: str
    entry_price: float
    exit_price: Optional[float] = None
    stop_loss: float
    take_profit: float
    volume_units: float
    pnl: Optional[float] = 0.0
    commission: Optional[float] = 0.0
    swap: Optional[float] = 0.0
    slippage_pips: Optional[float] = 0.0
    status: str
    fsm_state_at_entry: Optional[str] = None
    open_time_utc: Optional[datetime] = None
    close_time_utc: Optional[datetime] = None

class ControlActionRequest(BaseModel):
    action: str = Field(..., description="Action: KILL, RESUME, FLATTEN, UPDATE_CONFIG")
    reason: Optional[str] = "Manual operator override"
    new_config: Optional[StrategyConfigModel] = None

class MacroAnalysisRequest(BaseModel):
    symbol: str = "EURUSD"
    timeframe: str = "15M/1M"
    recent_ticks: Optional[List[Dict[str, Any]]] = None
    recent_trades: Optional[List[Dict[str, Any]]] = None
    custom_context: Optional[str] = None

class WfaRunResult(BaseModel):
    run_timestamp_utc: str
    in_sample_sharpe: float
    out_of_sample_sharpe: float
    deflated_sharpe_ratio: float
    max_drawdown_percent: float
    win_rate_percent: float
    promoted: bool
    recommended_parameters: Dict[str, Any]
