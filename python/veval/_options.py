import os
import warnings


class VevalOptions:
    def __init__(
        self,
        api_key: str = "",
        project_id: str = "",
        flush_interval_ms: int = 5000,
        flush_batch_size: int = 50,
    ):
        # The API key also determines the workspace.
        self.api_key = api_key
        if project_id:
            warnings.warn(
                "VevalOptions.project_id is ignored: the API key determines the workspace. "
                "It will be removed in a future version.",
                DeprecationWarning,
                stacklevel=2,
            )
        self.project_id = project_id
        # Veval's own hosted API, which enforces billing/quota (test-run limits, judge credits, etc.) —
        # not a self-hosted override for SDK consumers, so it isn't a public option.
        # VEVAL_INTERNAL_ENDPOINT is an undocumented escape hatch for Veval's own local development
        # against a non-production API instance.
        self._endpoint = os.environ.get("VEVAL_INTERNAL_ENDPOINT", "https://api.veval.dev")
        self.flush_interval_ms = flush_interval_ms
        self.flush_batch_size = flush_batch_size
