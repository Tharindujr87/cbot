CREATE TABLE IF NOT EXISTS trade_logs (
    id SERIAL PRIMARY KEY,
    ticket_id VARCHAR(64) UNIQUE NOT NULL,
    symbol VARCHAR(16) NOT NULL,
    trade_type VARCHAR(8) NOT NULL,
    entry_price NUMERIC(10, 5) NOT NULL,
    stop_loss NUMERIC(10, 5) NOT NULL,
    take_profit NUMERIC(10, 5) NOT NULL,
    volume_units NUMERIC(12, 2) NOT NULL,
    pnl NUMERIC(10, 2),
    status VARCHAR(20) NOT NULL,
    open_time_utc TIMESTAMP WITH TIME ZONE NOT NULL,
    close_time_utc TIMESTAMP WITH TIME ZONE
);

CREATE TABLE IF NOT EXISTS system_telemetry (
    timestamp TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    balance NUMERIC(10, 2) NOT NULL,
    equity NUMERIC(10, 2) NOT NULL,
    free_margin NUMERIC(10, 2) NOT NULL,
    current_spread NUMERIC(4, 2) NOT NULL,
    state VARCHAR(32) NOT NULL
);

CREATE TABLE IF NOT EXISTS error_logs (
    id SERIAL PRIMARY KEY,
    timestamp TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    severity VARCHAR(16) NOT NULL,
    source VARCHAR(32) NOT NULL,
    message TEXT NOT NULL
);