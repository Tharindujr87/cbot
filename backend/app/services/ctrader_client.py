import os
import json
import psycopg2
from ctrader_open_api import Client, Protobuf, TcpProtocol
from ctrader_open_api.messages.OpenApiCommonMessages_pb2 import *
from ctrader_open_api.messages.OpenApiMessages_pb2 import *

class HeadlessExecutionEngine:
    def __init__(self, config_path, env_file):
        self.client_id = os.getenv("CTRADER_CLIENT_ID")
        self.client_secret = os.getenv("CTRADER_CLIENT_SECRET")
        self.host = os.getenv("CTRADER_HOST", "demo.ctraderapi.com")
        self.port = int(os.getenv("CTRADER_PORT", 5035))
        self.config_path = config_path

        self.client = Client(self.host, self.port, TcpProtocol)
        self.client.setConnectedCallback(self.on_connected)
        self.client.setDisconnectedCallback(self.on_disconnected)
        self.client.setMessageReceivedCallback(self.on_message_received)

    def on_connected(self, client):
        print("[cTrader API] Connected to gateway. Authenticating application...")
        # Send Application Authorization
        req = ProtoOAApplicationAuthReq()
        req.clientId = self.client_id
        req.clientSecret = self.client_secret
        self.client.send(req)

    def on_disconnected(self, client, reason):
        print(f"[cTrader API] Disconnected: {reason}. Attempting reconnect...")

    def on_message_received(self, client, message):
        payload_type = message.payloadType
        
        # 1. App Auth Response
        if payload_type == ProtoOAApplicationAuthRes().payloadType:
            print("[cTrader API] Application authorized successfully.")
            # Trigger account authorization and symbol subscriptions here
            
        # 2. Execution Event (Order Fill, SL/TP Hit)
        elif payload_type == ProtoOAExecutionEvent().payloadType:
            event = ProtoOAExecutionEvent()
            event.ParseFromString(message.payload)
            self.log_execution_to_db(event)

        # 3. Market Data Event (Ticks/Bars)
        elif payload_type == ProtoOASpotEvent().payloadType:
            spot = ProtoOASpotEvent()
            spot.ParseFromString(message.payload)
            self.process_tick(spot)

    def log_execution_to_db(self, event):
        try:
            conn = psycopg2.connect(os.getenv("DATABASE_URL"))
            cur = conn.cursor()
            cur.execute("""
                INSERT INTO trade_logs (ticket_id, symbol, trade_type, entry_price, stop_loss, take_profit, volume_units, status, open_time_utc)
                VALUES (%s, %s, %s, %s, %s, %s, %s, %s, NOW())
                ON CONFLICT (ticket_id) DO UPDATE SET pnl = EXCLUDED.pnl, status = EXCLUDED.status;
            """, (
                str(event.order.orderId),
                "EURUSD",
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

    def process_tick(self, spot):
        # Passes tick data into the 15M sweep / 1M displacement state machine
        pass

    def start(self):
        print("[cTrader API] Starting TCP Twisted Reactor...")
        self.client.startService()