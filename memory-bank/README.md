# Memory bank

Persistent context for an AI agent working in this repository.

A fresh session arrives with the code but without the reasoning that produced it — and the
reasoning is where most of the value is. These files carry it across sessions.

**Read in numeric order.** `00` and `10` are enough to start; the rest is reference.

| File | What it holds |
|---|---|
| [`00-start-here.md`](00-start-here.md) | Orientation, and the things that will bite you first |
| [`10-architecture.md`](10-architecture.md) | How the pieces fit and the rules that hold them apart |
| [`20-monkeys.md`](20-monkeys.md) | The seven agents and how an investigation actually runs |
| [`30-conventions.md`](30-conventions.md) | House style, and the distinctions that are load-bearing |
| [`40-session-log.md`](40-session-log.md) | What was changed recently, and why — with measurements |
| [`50-open-issues.md`](50-open-issues.md) | What is broken, what is known-failing, what is next |
| [`60-kql-authoring-analysis.md`](60-kql-authoring-analysis.md) | Whether the LLM should write its own KQL — researched, with evidence |

## Keeping it current

When you finish a meaningful piece of work, update `40-session-log.md` and `50-open-issues.md`.
Record the measurement that justified a decision, not just the decision. A number with its source
survives being questioned six months later; an assertion does not.

If you discover that something here is wrong, fix it in place and say so in the session log. A
memory bank that is quietly wrong is worse than none.
