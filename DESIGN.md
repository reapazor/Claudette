# Claudette Design Goals

## Platform
- A .NET-based visual client that wraps Claude Code.
- Should be usable on Windows and macOS, Linux would be a bonus but not required.
- The intent is to provide a nicer visual experience for the user, utilizing native styling/windows of the given platform.


## Experience

### Tabs

- A tab should represent a unique controlled session of Claude Code.
- Each tab should by default use the name that Claude Code provides to the session/cli tab, but allow for users to rename their own.

### Token Burn Awareness

- Important to give a user a visual representation of how fast they are burning up their session / weekly / fable usage.
- 


- Allows multiple tabs representing independant instances of Claude Code


- An area to provide feedback to the selected tab of Claude Code, with an easy way to stop the current executing work.
- The header of the application should have an area that visually shows  your current work sessions token usage / when the session is set to reset.
- There should be a visible indicator of the weekly limits (albeit not as prominent).
- There should also be a trendline of how fast your burning a sessions limits and when at its given rate it wwill run out.
- Tabs should be by default renamed to the claude code desired name (how it names its CLI tabs) but should allow users to rename as well.
