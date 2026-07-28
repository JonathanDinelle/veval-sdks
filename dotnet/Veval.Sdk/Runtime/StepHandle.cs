namespace Veval.Sdk;

public class StepHandle
{
    private readonly Step _step;

    internal StepHandle(Step step) => _step = step;

    public void SetMeta(string key, object value)
    {
        switch (key)
        {
            case "tokens_in":  _step.TokensIn  = Convert.ToInt32(value);   break;
            case "tokens_out": _step.TokensOut = Convert.ToInt32(value);   break;
            case "cost_usd":   _step.CostUsd   = Convert.ToDecimal(value); break;
            case "model":      _step.Model     = value.ToString();         break;
            case "type":       _step.Type      = value.ToString()!;        break;
            default:           _step.SetMetadata(key, value);              break;
        }
    }
}
