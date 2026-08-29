import os
import json
import psycopg2
from datetime import datetime, timezone
from dotenv import load_dotenv

load_dotenv()

from ctrader_open_api import Client, Protobuf, TcpProtocol
from ctrader_open_api.messages.OpenApiCommonMessages_pb2 import *
from ctrader_open_api.messages.OpenApiMessages_pb2 import *

class HeadlessExecutionEngine:
    """
    cTrader Open API Execution & Real-Time Market Data Engine.
    Connects directly to Pepperstone / cTrader Open API gateway to fetch live EURUSD tick feeds.
    """
    def __init__(self, config_path="strategy_config.json", env_file=None):
        self.client_id = os.getenv("CTRADER_CLIENT_ID", "")
        self.client_secret = os.getenv("CTRADER_CLIENT_SECRET", "")
        self.account_id = int(os.getenv("CTRADER_ACCOUNT_ID", "0")) if os.getenv("CTRADER_ACCOUNT_ID") else None
        self.access_token = os.getenv("CTRADER_ACCESS_TOKEN", "")
        self.host = os.getenv("CTRADER_HOST", "demo.ctraderapi.com")
        self.port = int(os.getenv("CTRADER_PORT", 5035))
        self.config_path = config_path

        self.symbol_name = "EURUSD"
        self.symbol_id = 1 # Default EURUSD symbol ID on standard cTrader accounts
        self.digits = 5
        self.pip_scale = 100000.0

        self.client = Client(self.host, self.port, TcpProtocol)
        self.client.setConnectedCallback(self.on_connected)
        self.client.setDisconnectedCallback(self.on_disconnected)
        self.client.setMessageReceivedCallback(self.on_message_received)

    def on_connected(self, client):
        print(f"[cTrader Open API] Connected to {self.host}:{self.port}. Authenticating application...")
        # Step 1: Send Application Authorization
        req = ProtoOAApplicationAuthReq()
        req.clientId = self.client_id
        req.clientSecret = self.client_secret
        self.client.send(req)

    def on_disconnected(self, client, reason):
        print(f"[cTrader Open API] Disconnected: {reason}. Reconnecting in 5s...")

    def on_message_received(self, client, message):
        payload_type = message.payloadType

        # 1. Application Authorized Response
        if payload_type == ProtoOAApplicationAuthRes().payloadType:
            print("[cTrader Open API] Application authorized successfully.")
            if self.account_id and self.access_token:
                self.authorize_account()
            else:
                print("[cTrader Open API] Note: CTRADER_ACCOUNT_ID or CTRADER_ACCESS_TOKEN not set yet in .env.")
                print(f"[cTrader Open API] To get Access Token, visit: https://openapi.ctrader.com/apps")

        # 2. Account Authorized Response
        elif payload_type == ProtoOAAccountAuthRes().payloadType:
            print(f"[cTrader Open API] Account {self.account_id} authorized successfully! Fetching symbol list & subscribing to {self.symbol_name}...")
            self.fetch_symbols_and_subscribe()

        # 3. Symbols List Response
        elif payload_type == ProtoOASymbolsListRes().payloadType:
            res = ProtoOASymbolsListRes()
            res.ParseFromString(message.payload)
            for sym in res.symbol:
                if sym.symbolName == self.symbol_name:
                    self.symbol_id = sym.symbolId
                    self.digits = getattr(sym, 'digits', 5)
                    self.pip_scale = 100000.0
                    print(f"[cTrader Open API] Resolved {self.symbol_name} -> Symbol ID: {self.symbol_id}")
                    break
            self.subscribe_to_spots()

        # 4. Spot Market Data Event (Live Ticks)
        elif payload_type == ProtoOASpotEvent().payloadType:
            spot = ProtoOASpotEvent()
            spot.ParseFromString(message.payload)
            if spot.symbolId == self.symbol_id:
                self.process_tick(spot)

        # 5. Execution Event (Order Placed, Filled, SL/TP Hit)
        elif payload_type == ProtoOAExecutionEvent().payloadType:
            event = ProtoOAExecutionEvent()
            event.ParseFromString(message.payload)
            self.log_execution_to_db(event)

    def authorize_account(self):
        print(f"[cTrader Open API] Authorizing account {self.account_id}...")
        req = ProtoOAAccountAuthReq()
        req.ctidTraderAccountId = self.account_id
        req.accessToken = self.access_token
        self.client.send(req)

    def fetch_symbols_and_subscribe(self):
        req = ProtoOASymbolsListReq()
        req.ctidTraderAccountId = self.account_id
        self.client.send(req)

    def subscribe_to_spots(self):
        print(f"[cTrader Open API] Subscribing to live spots for Symbol ID: {self.symbol_id} ({self.symbol_name})...")
        req = ProtoOASubscribeSpotsReq()
        req.ctidTraderAccountId = self.account_id
        req.symbolId.append(self.symbol_id)
        self.client.send(req)

    def process_tick(self, spot):
        bid = spot.bid / self.pip_scale if spot.HasField("bid") else None
        ask = spot.ask / self.pip_scale if spot.HasField("ask") else None
        
        if bid and ask:
            spread_pips = round((ask - bid) * (self.pip_scale / 10.0), 1)
            # Update shared memory / IPC telemetry
            from .ipc_bridge import ipc_bridge_service
            ipc_bridge_service.latest_telemetry.update({
                "symbol": self.symbol_name,
                "bid": round(bid, 5),
                "ask": round(ask, 5),
                "spread_pips": spread_pips,
                "timestamp_utc": datetime.now(timezone.utc).isoformat()
            })
            if ipc_bridge_service.on_telemetry_callback:
                ipc_bridge_service.on_telemetry_callback(ipc_bridge_service.latest_telemetry)

    def log_execution_to_db(self, event):
        try:
            db_url = os.getenv("DATABASE_URL")
            if not db_url:
                return
            conn = psycopg2.connect(db_url)
            cur = conn.cursor()
            cur.execute("""
                INSERT INTO trade_logs (ticket_id, symbol, trade_type, entry_price, stop_loss, take_profit, volume_units, status, open_time_utc)
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, NOW())
                ON CONFLICT (ticket_id) DO UPDATE SET pnl = EXCLUDED.pnl, status = EXCLUDED.status;
            """, (
                str(event.order.orderId),
                self.symbol_name,
                "BUY" if event.order.tradeData.tradeSide == 1 else "SELL",
                event.order.executionPrice,
                event.order.stopLoss,
                event.order.takeProfit,
                event.order.tradeData.volume / 100.0,
                str(event.executionType)
            ))
            conn.commit()
            cur.close()
            conn.close()
        except Exception as e:
            print(f"[DB Error] Failed to log execution: {e}")

    def start(self):
        print(f"[cTrader Open API] Starting client connection to {self.host}:{self.port}...")
        self.client.startService()
        from twisted.internet import reactor
        if not reactor.running:
            reactor.run()

if __name__ == "__main__":
    engine = HeadlessExecutionEngine()
    engine.start()