using System;
using System.Collections.Generic;
using System.Text;

public sealed class EVAStableTranscript
{
    private readonly List<string> committedKeys = new();
    private readonly StringBuilder text = new();
    private string[] previousKeys = Array.Empty<string>();

    public string Text => text.ToString();

    public void Clear()
    {
        committedKeys.Clear();
        text.Clear();
        previousKeys = Array.Empty<string>();
    }

    public bool Accept(string hypothesis, bool isFinal)
    {
        string[] words = (hypothesis ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        string[] keys = new string[words.Length];
        for (int i = 0; i < words.Length; i++) keys[i] = WordKey(words[i]);

        int agreed = isFinal ? words.Length : 0;
        if (!isFinal)
        {
            while (agreed < keys.Length && agreed < previousKeys.Length && keys[agreed] == previousKeys[agreed])
                agreed++;
        }
        previousKeys = keys;
        if (agreed == 0) return false;

        int consumed = FindConsumedPrefix(keys, agreed);
        if (consumed >= agreed) return false;
        for (int i = consumed; i < agreed; i++)
        {
            if (text.Length > 0) text.Append(' ');
            text.Append(words[i]);
            committedKeys.Add(keys[i]);
        }
        return true;
    }

    private int FindConsumedPrefix(string[] keys, int count)
    {
        if (committedKeys.Count == 0) return 0;
        int shared = Math.Min(committedKeys.Count, count);
        int matched = 0;
        while (matched < shared && committedKeys[matched] == keys[matched]) matched++;
        if (matched == shared) return shared;

        int[] previous = new int[count + 1];
        int[] current = new int[count + 1];
        for (int j = 0; j <= count; j++) previous[j] = j;
        for (int i = 1; i <= committedKeys.Count; i++)
        {
            current[0] = i;
            for (int j = 1; j <= count; j++)
            {
                int replace = previous[j - 1] + (committedKeys[i - 1] == keys[j - 1] ? 0 : 1);
                current[j] = Math.Min(replace, Math.Min(previous[j] + 1, current[j - 1] + 1));
            }
            var swap = previous;
            previous = current;
            current = swap;
        }
        int best = 0;
        for (int j = 1; j <= count; j++)
        {
            if (previous[j] <= previous[best]) best = j;
        }
        int anchored = -1;
        string lastKey = committedKeys[committedKeys.Count - 1];
        for (int j = best; j <= count; j++)
            if (j > 0 && keys[j - 1] == lastKey && (anchored < 0 || previous[j] < previous[anchored]))
                anchored = j;
        if (anchored >= 0) best = anchored;
        return best;
    }

    private static string WordKey(string word)
    {
        var key = new StringBuilder(word.Length);
        foreach (char character in word)
            if (char.IsLetterOrDigit(character)) key.Append(char.ToLowerInvariant(character));
        return key.Length > 0 ? key.ToString() : word;
    }
}
