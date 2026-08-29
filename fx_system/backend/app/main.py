import os
import json
import asyncio
from dotenv import load_dotenv

load_dotenv()

from contextlib import asynccontextmanager
from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware
from .api.routes_control import router as control_router
from .api.routes_telemetry import router as telemetry_router
from .api.routes_llm import router as llm_router
from .services.ipc_bridge import ipc_bridge_service

active_websockets = set()

def broadcast_telemetry(telemetry_data: dict):
    # When new telemetry arrives from cBot via IPC, push to all active WebSockets
    if active_websockets:
        message = json.dumps({"type": "TELEMETRY_UPDATE", "data": telemetry_data})
        for ws in list(active_websockets):
            try:
                asyncio.run_coroutine_threadsafe(ws.send_text(message), loop=asyncio.get_event_loop())
            except Exception:
                pass

@asynccontextmanager
async def lifespan(app: FastAPI):
    # Startup: Start ZeroMQ / IPC telemetry bridge
    ipc_bridge_service.on_telemetry_callback = broadcast_telemetry
    ipc_bridge_service.start()
    print("[FastAPI] Forex Bot Cognitive & Telemetry Engine Online.")
    yield
    # Shutdown
    ipc_bridge_service.stop()
    print("[FastAPI] Forex Bot Backend shutting down.")

app = FastAPI(
    title="Autonomous Forex Execution & Telemetry Daemon",
    description="Cognitive Plane API, Walk-Forward Analysis & Remote Control System for cTrader Automate Bot",
    version="2.0.0",
    lifespan=lifespan
)

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

app.include_router(control_router)
app.include_router(telemetry_router)
app.include_router(llm_router)

@app.get("/")
def root():
    return {
        "system": "Autonomous Forex Execution & Telemetry Daemon",
        "version": "2.0.0",
        "status": "OPERATIONAL",
        "docs": "/docs"
    }

@app.websocket("/ws/telemetry")
async def websocket_telemetry_stream(websocket: WebSocket):
    await websocket.accept()
    active_websockets.add(websocket)
    try:
        # Send initial state immediately
        await websocket.send_text(json.dumps({
            "type": "INITIAL_STATE",
            "data": ipc_bridge_service.latest_telemetry
        }))
        while True:
            # Keep connection alive, listen for ping or client events
            data = await websocket.receive_text()
    except WebSocketDisconnect:
        active_websockets.discard(websocket)
    except Exception:
        active_websockets.discard(websocket)

if __name__ == "__main__":
    import uvicorn
    uvicorn.run("backend.app.main:app", host="127.0.0.1", port=8000, reload=True)
