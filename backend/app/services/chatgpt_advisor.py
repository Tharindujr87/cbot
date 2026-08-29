import os
import json
from typing import Dict, Any, Optional

class ChatGPTAdvisor:
    """
    ChatGPT (OpenAI GPT-4o) Quantitative Macro & Trade Debrief Advisory Service.
    Operates strictly asynchronously and out-of-band to prevent blocking order execution.
    """
    def __init__(self):
        self.api_key = os.getenv("OPENAI_API_KEY", "")

    async def generate_macro_regime_analysis(self, context_payload: Dict[str, Any]) -> Dict[str, Any]:
        """Generates macro regime context and high-impact risk commentary."""
        if not self.api_key:
            # Fallback simulated response for development / demo mode
            return {
                "status": "SIMULATED",
                "macro_regime": "NEUTRAL_TO_BULLISH",
                "volatility_index": "MODERATE",
                "high_impact_news_risk": "LOW (No tier-1 events in next 2 hours)",
                "structural_health_score": 88,
                "summary": "EUR/USD is consolidating above the Asian range low. Liquidity sweeps at London open showed rapid rejection. Market structure shift on 1M suggests strong demand with clean Fair Value Gaps."
            }

        try:
            import openai
            openai.api_key = self.api_key

            prompt = (
                f"You are a quantitative macro risk advisor. Review the latest market state: {json.dumps(context_payload)}. "
                "Provide a structured JSON output with: 'macro_regime', 'volatility_index', 'high_impact_news_risk', "
                "'structural_health_score' (0-100), and a 2-sentence 'summary' of institutional order flow."
            )

            client = openai.OpenAI(api_key=self.api_key)
            response = client.chat.completions.create(
                model="gpt-4o",
                messages=[
                    {"role": "system", "content": "You are a quantitative risk management advisor for institutional forex trading."},
                    {"role": "user", "content": prompt}
                ],
                response_format={"type": "json_object"},
                max_tokens=300
            )
            return json.loads(response.choices[0].message.content)
        except Exception as e:
            return {
                "status": "ERROR",
                "error": str(e),
                "summary": "Macro advisor encountered an error connecting to OpenAI API."
            }

    async def generate_trade_debrief(self, trade_data: Dict[str, Any]) -> Dict[str, Any]:
        """Provides post-trade analysis on entry precision, slippage, and FSM compliance."""
        if not self.api_key:
            return {
                "status": "SIMULATED",
                "trade_id": trade_data.get("ticket_id", "T-1001"),
                "execution_rating": "OPTIMAL",
                "slippage_impact": "MINIMAL (0.2 pips)",
                "fsm_compliance": "100% adherence to 15M sweep + 1M MSS rules",
                "debrief": "The trade respected the 15M session low sweep and entered precisely at the 1M FVG retest. Profit target was achieved with zero drawdown beyond the planned stop-loss buffer."
            }

        try:
            import openai
            client = openai.OpenAI(api_key=self.api_key)
            prompt = f"Analyze this executed trade for rule compliance and execution slippage: {json.dumps(trade_data)}"
            response = client.chat.completions.create(
                model="gpt-4o",
                messages=[
                    {"role": "system", "content": "You are an algorithmic execution auditor."},
                    {"role": "user", "content": prompt}
                ],
                max_tokens=250
            )
            return {"debrief": response.choices[0].message.content}
        except Exception as e:
            return {"status": "ERROR", "error": str(e)}

chatgpt_advisor_service = ChatGPTAdvisor()
