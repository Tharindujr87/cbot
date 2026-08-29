-- Database Initialization Script for Forex Bot Telemetry & Management

CREATE TABLE IF NOT EXISTS trade_logs (
    id SERIAL PRIMARY KEY,
    ticket_id VARCHAR(64) UNIQUE NOT NULL,
    symbol VARCHAR(16) NOT NULL,
    trade_type VARCHAR(8) NOT NULL, -- BUY, SELL
    entry_price NUMERIC(12, 5) NOT NULL,
    exit_price NUMERIC(12, 5),
    stop_loss NUMERIC(12, 5) NOT NULL,
    take_profit NUMERIC(12, 5) NOT NULL,
    volume_units NUMERIC(12, 2) NOT NULL,
    pnl NUMERIC(12, 2) DEFAULT 0.00,
    commission NUMERIC(10, 2) DEFAULT 0.00,
    swap NUMERIC(10, 2) DEFAULT 0.00,
    slippage_pips NUMERIC(6, 2) DEFAULT 0.00,
    status VARCHAR(32) NOT NULL, -- PENDING, OPEN, CLOSED_TP, CLOSED_SL, CANCELLED
    fsm_state_at_entry VARCHAR(64),
    open_time_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    close_time_utc TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS telemetry_ticks (
    id BIGSERIAL PRIMARY KEY,
    symbol VARCHAR(16) NOT NULL,
    bid NUMERIC(12, 5) NOT NULL,
    ask NUMERIC(12, 5) NOT NULL,
    spread_pips NUMERIC(6, 2) NOT NULL,
    fsm_state VARCHAR(64) NOT NULL,
    free_margin NUMERIC(12, 2),
    margin_level_percent NUMERIC(8, 2),
    account_equity NUMERIC(12, 2),
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS strategy_audit_logs (
    id SERIAL PRIMARY KEY,
    modified_by VARCHAR(64) NOT NULL, -- USER, WFA_ENGINE, CIRCUIT_BREAKER
    action VARCHAR(64) NOT NULL,      -- CONFIG_UPDATE, KILL_SWITCH, RESUME
    previous_config JSONB,
    new_config JSONB NOT NULL,
    notes TEXT,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS wfa_runs (
    id SERIAL PRIMARY KEY,
    run_timestamp_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    in_sample_start TIMESTAMPTZ NOT NULL,
    in_sample_end TIMESTAMPTZ NOT NULL,
    out_of_sample_start TIMESTAMPTZ NOT NULL,
    out_of_sample_end TIMESTAMPTZ NOT NULL,
    tested_parameters JSONB NOT NULL,
    best_parameters JSONB NOT NULL,
    in_sample_sharpe NUMERIC(8, 3),
    out_of_sample_sharpe NUMERIC(8, 3),
    deflated_sharpe_ratio NUMERIC(8, 3),
    max_drawdown_percent NUMERIC(6, 2),
    win_rate_percent NUMERIC(6, 2),
    is_promoted BOOLEAN NOT NULL DEFAULT FALSE
);

CREATE TABLE IF NOT EXISTS system_events (
    id SERIAL PRIMARY KEY,
    event_level VARCHAR(16) NOT NULL, -- INFO, WARNING, ERROR, CRITICAL
    component VARCHAR(64) NOT NULL,   -- CBOT, BACKEND, IPC, OPENAI, DB
    message TEXT NOT NULL,
    metadata JSONB,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS idx_trade_logs_ticket ON trade_logs(ticket_id);
CREATE INDEX IF NOT EXISTS idx_trade_logs_open_time ON trade_logs(open_time_utc DESC);
CREATE INDEX IF NOT EXISTS idx_telemetry_created_at ON telemetry_ticks(created_at_utc DESC);
CREATE INDEX IF NOT EXISTS idx_system_events_level ON system_events(event_level, created_at_utc DESC);
