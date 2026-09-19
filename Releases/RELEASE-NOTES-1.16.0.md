# Quest Tracker 1.16.0

Install over 1.14.0 (1.15.0 was built but never published; everything in it is here) - replace both
DLLs, keep your config.

## The toolbar fits at 1600 px

The bar now checks whether its six status chips, the "N shown" notice and the view buttons can all
fit. When they cannot, the chips fold into one **Legend** item showing the six status glyphs in their
colours, with the full names on hover, and the view buttons take their minimum width. At 1920 wide
nothing changes. At 1600 the notice gets its room back. At 1366 nothing overlaps any more, but the
notice is still short.

## Prices in dollars and euros are roubles now

A part sold by Peacekeeper or Ref showed its price in dollars or euros with a rouble label, on the
build panel's cost lines and in the totals, since the builder first shipped. They convert through the
game's own exchange rate now. The measurement below is what exposed it.

## Under the hood

- The server logs, per profile, how the weapon builder's objective (handbook prices) compares with
  what you would actually pay (trader prices and flea estimates) over the shared builds with
  something to buy, and how many builds the two price more than 25% apart. A measurement, not a
  change: it decides whether the objective is ever worth changing.
