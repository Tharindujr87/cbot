from fastapi import APIRouter
from ..models.schema import MacroAnalysisRequest
from ..services.chatgpt_advisor import chatgpt_advisor_service
from ..services.ipc_bridge import ipc_bridge_service

router = APIRouter(prefix="/api/advisor", tags=["ChatGPT AI Macro Advisor"])

@router.post("/macro-debrief")
async def get_macro_debrief(payload: MacroAnalysisRequest):
    data = payload.model_dump()
    if not data.get("recent_ticks"):
        data["current_tick"] = ipc_bridge_service.latest_telemetry
    return await chatgpt_advisor_service.generate_macro_regime_analysis(data)

@router.post("/trade-debrief")
async def get_trade_debrief(trade_payload: dict):
    return await chatgpt_advisor_service.generate_trade_debrief(trade_payload)
