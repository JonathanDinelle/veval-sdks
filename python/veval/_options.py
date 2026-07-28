class VevalOptions:
    def __init__(
        self,
        api_key: str = "",
        project_id: str = "",
        flush_interval_ms: int = 5000,
        flush_batch_size: int = 50,
    ):
        self.api_key = api_key
        self.project_id = project_id
        self.endpoint = "https://api.veval.dev"
        self.flush_interval_ms = flush_interval_ms
        self.flush_batch_size = flush_batch_size
