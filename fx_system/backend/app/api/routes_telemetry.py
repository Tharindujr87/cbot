import os
import psycopg2
import psycopg2.extras
from datetime import datetime, timezone
from typing import List
from fastapi import APIRouter
from ..models.schema import TelemetryTickModel, TradeLogModel, WfaRunResult
from ..services.ipc_bridge import ipc_bridge_service
from ..services.wfa_engine import wfa_engine_service

router = APIRouter(prefix="/api/telemetry", tags=["Telemetry & Performance"])

def query_real_trades_from_db() -> List[TradeLogModel]:
    db_url = os.getenv("DATABASE_URL")
    if not db_url:
        return []
    try:
        conn = psycopg2.connect(db_url)
        cur = conn.cursor(cursor_factory=psycopg2.extras.DictCursor)
        cur.execute("""
            SELECT ticket_id, symbol, trade_type, 
                   COALESCE(entry_price, 0.0) as entry_price, 
                   COALESCE(exit_price, entry_price) as exit_price,
                   COALESCE(stop_loss, 0.0) as stop_loss, 
                   COALESCE(take_profit, 0.0) as take_profit, 
                   COALESCE(volume_units, 0.0) as volume_units, 
                   COALESCE(pnl, 0.0) as pnl, 
                   COALESCE(commission, 0.0) as commission, 
                   COALESCE(swap, 0.0) as swap, 
                   COALESCE(slippage_pips, 0.0) as slippage_pips, 
                   status, fsm_state_at_entry, open_time_utc, close_time_utc
            FROM trade_logs
            ORDER BY open_time_utc DESC
            LIMIT 50;
        """)
        rows = cur.fetchall()
        cur.close()
        conn.close()
        
        trades = []
        for r in rows:
            trades.append(TradeLogModel(
                ticket_id=str(r["ticket_id"]),
                symbol=str(r["symbol"]),
                trade_type=str(r["trade_type"]),
                entry_price=float(r["entry_price"]),
                exit_price=float(r["exit_price"]) if r["exit_price"] is not None else None,
                stop_loss=float(r["stop_loss"]),
                take_profit=float(r["take_profit"]),
                volume_units=float(r["volume_units"]),
                pnl=float(r["pnl"]),
                commission=float(r["commission"]),
                swap=float(r["swap"]),
                slippage_pips=float(r["slippage_pips"]),
                status=str(r["status"]),
                fsm_state_at_entry=r["fsm_state_at_entry"],
                open_time_utc=r["open_time_utc"],
                close_time_utc=r["close_time_utc"]
            ))
        return trades
    except Exception as e:
        print(f"[DB Query Error] {e}")
        return []

@router.get("/tick", response_model=TelemetryTickModel)
def get_latest_telemetry_tick():
    return TelemetryTickModel(**ipc_bridge_service.latest_telemetry)

@router.get("/trades", response_model=List[TradeLogModel])
def get_trade_logs():
    return query_real_trades_from_db()

@router.post("/wfa/trigger", response_model=WfaRunResult)
def trigger_wfa_optimization():
    return wfa_engine_service.run_optimization_cycle()
