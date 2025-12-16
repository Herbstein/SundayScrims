## Thoughts for workflow:

On `EventGameStart`, look at all players and their current elo. Create and assign teams. Insert active game into DB. Remove
any unfinished games.

On `EventRoundEnd`, update current game stats in database

On `EventGameEnd`, calculate new player ratings based on round data gathered during game

This workflow allows for the plugin to be updated/reloaded at any time.

When new players join, they're assigned to the underpowered team

## Open questions

* Do we store each game round with foreign keys to the players?
    * Do players that join mid-game inhereit the bad rounds, or only get re-ranked with the rounds they participated in?

