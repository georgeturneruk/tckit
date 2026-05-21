# Comment style

Two styles are accepted.

## RST line comments (preferred for new code)

```
// :Description:  What this does.
// :param x:      What x is for.
// :returns:      What comes back.
```

The doc generator detects these and renders them into HTML docs.

## Beckhoff XML

```
(*~ <docu>
  <p>What this does.</p>
</docu> ~*)
```

Also detected by the doc generator. Heavier; only use when matching
existing files.

## Rule

Match the file's existing style. A file with RST comments stays
RST; a file with Beckhoff XML stays Beckhoff XML. Mixing in a
single file is fine if the doc generator picks both up, but
discouraged for new code: pick one.

## Inline comments

Use sparingly. Code should be self-explanatory through naming;
comments should explain *why* something is the way it is, not
*what* the code does. A comment that paraphrases the next line is
noise.
