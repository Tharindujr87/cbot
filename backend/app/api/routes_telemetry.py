import os
from datetime import datetime, timezone
from typing import List
from fastapi import APIRouter
from ..models.schema import TelemetryTickModel, TradeLogModel, WfaRunResult
from ..services.ipc_bridge import ipc_bridge_service
from ..services.wfa_engine import wfa_engine_service

router = APIRouter(prefix="/api/telemetry", tags=["Telemetry & Performance"])

# Mock recent trade data for fast startup / dashboard display
MOCK_TRADES: List[TradeLogModel] = [
    TradeLogModel(
        ticket_id="CT-94821",
        symbol="EURUSD",
        trade_type="BUY",
        entry_price=1.08450,
        exit_price=1.08720,
        stop_loss=1.08380,
        take_profit=1.08720,
        volume_units=100000.0,
        pnl=270.00,
        commission=-3.50,
        swap=0.00,
        slippage_pips=0.1,
        status="CLOSED_TP",
        fsm_state_at_entry="FVG_LIMIT_FILLED",
        open_time_utc=datetime.now(timezone.utc),
        close_time_utc=datetime.now(timezone.utc)
    ),
    TradeLogModel(
        ticket_id="CT-94822",
        symbol="EURUSD",
        trade_type="SELL",
        entry_price=1.08820,
        exit_price=1.08640,
        stop_loss=1.08900,
        take_profit=1.08640,
        volume_units=100000.0,
        pnl=180.00,
        commission=-3.50,
        swap=0.00,
        slippage_pips=0.2,
        status="CLOSED_TP",
        fsm_state_at_entry="FVG_LIMIT_FILLED",
        open_time_utc=datetime.now(timezone.utc),
        close_time_utc=datetime.now(timezone.utc)
    )
]

@router.get("/tick", response_model=TelemetryTickModel)
def get_latest_telemetry_tick():
    return TelemetryTickModel(**ipc_bridge_service.latest_telemetry)

@router.get("/trades", response_model=List[TradeLogModel])
def get_trade_logs():
    return MOCK_TRADES

@router.post("/wfa/trigger", response_model=WfaRunResult)
def trigger_wfa_optimization():
    return wfa_engine_service.run_optimization_cycle()
