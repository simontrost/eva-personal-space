public sealed class EVAWordStream
{
    private string target = "";
    private int visibleLength;
    private bool complete;
    public string VisibleText => target.Substring(0, visibleLength).TrimEnd();

    public void Clear() { target = ""; visibleLength = 0; complete = false; }

    public void SetTarget(string value, bool isComplete)
    {
        value = value ?? "";
        int common = 0;
        while (common < visibleLength && common < value.Length && target[common] == value[common]) common++;
        if (common < visibleLength)
        {
            while (common > 0 && (common >= value.Length || !char.IsWhiteSpace(value[common]))) common--;
            visibleLength = common;
        }
        target = value;
        complete = isComplete;
    }

    public bool TryAdvance(out string text)
    {
        text = VisibleText;
        int start = visibleLength;
        while (start < target.Length && char.IsWhiteSpace(target[start])) start++;
        if (start == target.Length) return false;
        int end = start;
        while (end < target.Length && !char.IsWhiteSpace(target[end])) end++;
        if (end == target.Length && !complete) return false;
        visibleLength = end;
        text = VisibleText;
        return true;
    }
}
