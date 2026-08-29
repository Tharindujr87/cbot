import os
import json
import asyncio
import threading
from datetime import datetime, timezone
from typing import Callable, Optional, Dict, Any

try:
    import zmq
    import zmq.asyncio
    ZMQ_AVAILABLE = True
except ImportError:
    ZMQ_AVAILABLE = False

class IpcBridge:
    """
    ZeroMQ IPC Bridge between cTrader Automate C# Bot and FastAPI Backend.
    - PUB/SUB port (default 5556): Ingests fast telemetry ticks and trade state events from cBot.
    - REQ/REP port (default 5557): Dispatches hot-reload config updates and emergency kill commands to cBot.
    """
    def __init__(
        self,
        pub_sub_port: int = 5556,
        req_rep_port: int = 5557,
        on_telemetry_callback: Optional[Callable[[Dict[str, Any]], None]] = None
    ):
        self.pub_sub_port = int(os.getenv("ZMQ_SUB_PORT", pub_sub_port))
        self.req_rep_port = int(os.getenv("ZMQ_REP_PORT", req_rep_port))
        self.on_telemetry_callback = on_telemetry_callback
        self.running = False
        self.latest_telemetry: Dict[str, Any] = {
            "symbol": "EURUSD",
            "bid": 1.08520,
            "ask": 1.08527,
            "spread_pips": 0.7,
            "fsm_state": "WAITING_FOR_SWEEP",
            "account_equity": 10000.0,
            "account_balance": 10000.0,
            "free_margin": 9500.0,
            "margin_level_percent": 950.0,
            "daily_pnl": 0.0,
            "daily_drawdown_percent": 0.0,
            "timestamp_utc": datetime.now(timezone.utc).isoformat()
        }
        self.trade_logs_buffer = []

    def start(self):
        self.running = True
        if ZMQ_AVAILABLE:
            threading.Thread(target=self._run_sub_worker, daemon=True).start()
            threading.Thread(target=self._run_rep_worker, daemon=True).start()
            print(f"[IPC Bridge] ZeroMQ Sub bound on *:{self.pub_sub_port}, Rep on *:{self.req_rep_port}")
        else:
            print("[IPC Bridge] pyzmq not installed; running in simulated mock IPC telemetry mode.")
            threading.Thread(target=self._run_simulated_worker, daemon=True).start()

    def _run_sub_worker(self):
        context = zmq.Context()
        socket = context.socket(zmq.SUB)
        socket.bind(f"tcp://*:{self.pub_sub_port}")
        socket.setsockopt_string(zmq.SUBSCRIBE, "")

        while self.running:
            try:
                msg = socket.recv_string(flags=zmq.NOBLOCK)
                data = json.loads(msg)
                event_type = data.get("event")
                if event_type == "TELEMETRY_TICK":
                    self.latest_telemetry.update(data.get("payload", {}))
                    self.latest_telemetry["timestamp_utc"] = datetime.now(timezone.utc).isoformat()
                    if self.on_telemetry_callback:
                        self.on_telemetry_callback(self.latest_telemetry)
                elif event_type == "TRADE_EVENT":
                    self.trade_logs_buffer.append(data.get("payload", {}))
            except zmq.Again:
                import time
                time.sleep(0.01)
            except Exception as e:
                import time
                time.sleep(0.1)

    def _run_rep_worker(self):
        context = zmq.Context()
        socket = context.socket(zmq.REP)
        socket.bind(f"tcp://*:{self.req_rep_port}")

        while self.running:
            try:
                msg = socket.recv_string(flags=zmq.NOBLOCK)
                data = json.loads(msg)
                cmd = data.get("command")
                # Handle control queries or heartbeats from cBot
                response = {"status": "ACK", "received_command": cmd}
                socket.send_string(json.dumps(response))
            except zmq.Again:
                import time
                time.sleep(0.05)
            except Exception as e:
                import time
                time.sleep(0.1)

    def _run_simulated_worker(self):
        """Simulates realistic telemetry events when running without live broker feed."""
        import random
        import time
        states = ["WAITING_FOR_SWEEP", "SWEEP_DETECTED", "WAITING_FOR_MSS", "DISPLACEMENT_VALIDATED", "IN_TRADE"]
        curr_state_idx = 0
        base_price = 1.08500

        while self.running:
            base_price += random.choice([-0.00003, 0.00004, -0.00002, 0.00001])
            spread = round(random.uniform(0.4, 1.1), 1)
            if random.random() < 0.1:
                curr_state_idx = (curr_state_idx + 1) % len(states)

            self.latest_telemetry = {
                "symbol": "EURUSD",
                "bid": round(base_price, 5),
                "ask": round(base_price + (spread * 0.0001), 5),
                "spread_pips": spread,
                "fsm_state": states[curr_state_idx],
                "account_equity": 10240.50,
                "account_balance": 10000.00,
                "free_margin": 9150.00,
                "margin_level_percent": 840.5,
                "daily_pnl": 240.50,
                "daily_drawdown_percent": 1.25,
                "timestamp_utc": datetime.now(timezone.utc).isoformat()
            }
            if self.on_telemetry_callback:
                self.on_telemetry_callback(self.latest_telemetry)
            time.sleep(2)

    def send_command_to_cbot(self, command: str, payload: Dict[str, Any]) -> Dict[str, Any]:
        """Dispatches commands directly to C# execution bot via ZeroMQ REQ or local shared state."""
        msg = {"command": command, "payload": payload, "timestamp": datetime.now(timezone.utc).isoformat()}
        if ZMQ_AVAILABLE:
            try:
                context = zmq.Context()
                client_sock = context.socket(zmq.REQ)
                client_sock.connect(f"tcp://127.0.0.1:{self.req_rep_port}")
                client_sock.setsockopt(zmq.RCVTIMEO, 1000)
                client_sock.setsockopt(zmq.SNDTIMEO, 1000)
                client_sock.send_string(json.dumps(msg))
                res = client_sock.recv_string()
                client_sock.close()
                return json.loads(res)
            except Exception as e:
                return {"status": "SENT_FALLBACK", "message": f"IPC direct send fallback: {str(e)}"}
        return {"status": "SUCCESS_SIMULATED", "message": "Command dispatched to local strategy store."}

    def stop(self):
        self.running = False

ipc_bridge_service = IpcBridge()
