# Quest Tracker 1.18.0

Install over 1.17.0 - replace both DLLs and, if you want the re-seeded builds, the
`cache\weapon-builds.json` too (your server will otherwise carry its own history forward and
re-measure it under the new prices, which is fine, just slower to converge). Keep your config.

## The builder minimises what you pay

Until now the Gunsmith build search minimised handbook prices, and the 1.16.0 measurement showed
players paying 25 to 45 percent more than that. The search now prices every part at the cheapest
trader cash price at any loyalty level, in roubles, and prices a part no trader sells for cash at
three times handbook, so it reaches for a trader-sold part when one satisfies the quest. On the
reference profile the objective landed within 0.1 percent of the real bill, and an hour of training
found 9 cheaper builds. The shipped build history is from that run.

## GP coins are money

Ref prices all his stock in GP coins and the mod treated them as barter, so nothing Ref sold could
ever be "buyable". They convert at the game's own rate now.

## Also

- Fence is no longer a trader source: his stock rotates, and a part he happened to hold read as
  buyable at a price that vanished on his next restock.
- Flea estimates refresh every minute rather than being frozen at the first request.
- `train.ps1` works from any folder; `train-all-threads.cmd` trains on every thread in a console.
