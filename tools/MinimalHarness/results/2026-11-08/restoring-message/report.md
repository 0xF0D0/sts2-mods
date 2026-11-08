# Restoration message UI

Displays centered localized white restoration text on a black CanvasLayer owned by NGame. It survives old combat UI cleanup and replacement, stays visible while peers replay/compare results, and is removed before normal fade-in. The operation finally block also removes it on cancellation/failure. No voting or replay readiness condition was added.

Native source audited: NGame/RootSceneContainer and Transition, NTransition FadeOut/FadeIn (including Instant mode), MegaLabel.RefreshFont and FontManager.GetSubstituteFont in installed v0.111.0 / 41cef1ea. No new reflection members or Harmony targets.

The initial visible UI run PID 87544 failed the explicit Korean glyph-coverage check because the root theme font lacks Korean glyphs. Switched to the game's native locale font substitution; did not weaken the glyph test. That failed run also logged UI_RESTORING_HIDE on the cancellation cleanup path.

Focused native-rendered run PID 88212: PASS. Four actual viewport-triggered undos in one fight in Korean, English, Simplified Chinese and Traditional Chinese. Held an actual replay PlayCardAction until capturing each screen. Verified message visibility after NRun recreation, exact localized text, every font glyph, full viewport layout, node removal on completion, exact target state and continued live play. Four show/hide markers; four PNG captures. Korean and Simplified Chinese captures visually reviewed.

Separate four-process ENet phase integration: PASS. PIDs 88732, 88771, 88794, 88801. Four repeated undos per peer with proposer rotation, in-flight votes, slow replay, absent local recorder, stale checksum injection and continued native actions. Four show/hide pairs on every peer, zero divergences, no transport replacements or connection changes, final native hashes matched. Shared verifier summary retained.

Both successful runs executed the identical DLL hash recorded in evidence.json. Release build with warnings as errors passed. MinimalCheck checks the unchanged 16 Harmony targets and 14 reflection surfaces. Original mods/account settings were restored and hashes verified after each run; no task game process or occupied ENet port remains.

Only local validation was performed. This UI addition has not been committed or published. Unrelated AGENTS.md and draft assets are preserved.
