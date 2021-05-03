﻿using PKHeX.Core;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using static SysBot.Base.SwitchButton;
using static SysBot.Base.SwitchStick;
using static SysBot.Pokemon.PokeDataOffsets;

namespace SysBot.Pokemon
{
    public class OverworldScan : PokeRoutineExecutor
    {
        private readonly PokeTradeHub<PK8> Hub;
        private readonly BotCompleteCounts Counts;
        private readonly IDumper DumpSetting;
        private readonly int[] DesiredIVs;
        private readonly byte[] BattleMenuReady = { 0, 0, 0, 255 };

        public OverworldScan(PokeBotState cfg, PokeTradeHub<PK8> hub) : base(cfg)
        {
            Hub = hub;
            Counts = Hub.Counts;
            DumpSetting = Hub.Config.Folder;
            DesiredIVs = StopConditionSettings.InitializeTargetIVs(Hub);
        }

        private int encounterCount;

        public override async Task MainLoop(CancellationToken token)
        {
            Log("Identifying trainer data of the host console.");
            await IdentifyTrainer(token).ConfigureAwait(false);

            Log("Starting main EncounterBot loop.");
            Config.IterateNextRoutine();

            // Clear out any residual stick weirdness.
            await ResetStick(token).ConfigureAwait(false);

            var task = Hub.Config.SWSH_ScanBot.EncounteringType switch
            {
                ScanMode.Articuno => DoSeededEncounter(token, (EncounterType)7),
                ScanMode.Zapdos => DoSeededEncounter(token, (EncounterType)8),
                ScanMode.Moltres => DoSeededEncounter(token, (EncounterType)9),
                ScanMode.Wailord => DoSeededEncounter(token, (EncounterType)10),
                ScanMode.OverworldSpawn => Overworld(token),
                _ => Overworld(token),
            };
            await task.ConfigureAwait(false);

            await ResetStick(token).ConfigureAwait(false);
            await DetachController(token).ConfigureAwait(false);
        }

        private async Task FlyToRerollSeed(EncounterType encounter, CancellationToken token)
        {
            bool exit = false;

            while (!exit)
            {
                await Click(X, 2_000, token).ConfigureAwait(false);
                await Click(PLUS, 5_000, token).ConfigureAwait(false);

                if (encounter == (EncounterType)9 || encounter == (EncounterType)10)
                    await Click(DLEFT, 0_500, token).ConfigureAwait(false);
                else if (encounter == (EncounterType)7)
                {
                    await PressAndHold(DDOWN, 0_150, 1_000, token).ConfigureAwait(false);
                    await PressAndHold(DRIGHT, 0_090, 1_000, token).ConfigureAwait(false);
                }

                for (int i = 0; i < 5; i++)
                    await Click(A, 1_000, token).ConfigureAwait(false);

                await Task.Delay(2_500, token).ConfigureAwait(false);

                if (encounter != (EncounterType)7 || (encounter == (EncounterType)7 && await IsArticunoPresent(token).ConfigureAwait(false)))
                    exit = true;
                else
                    Log("Articuno not found on path.");
            }

            await Click(X, 2_000, token).ConfigureAwait(false);
            await Click(R, 2_000, token).ConfigureAwait(false);
            await Click(A, 5_000, token).ConfigureAwait(false);

            Log("Game saved, seed rerolled.");
        }

        private async Task Overworld(CancellationToken token)
        {
            //THIS ROUTINE IS A PROOF OF CONCEPT, WORKING AT THE OLD CEMETERY IN THE CROWN TUNDRA
            SAV8 sav = await GetFakeTrainerSAV(token).ConfigureAwait(false);
            byte[] KCoordinates = await ReadKCoordinates(token).ConfigureAwait(false);
            while (!token.IsCancellationRequested)
            {
                if (await IsInBattle(token).ConfigureAwait(false))
                {
                    // Offsets are flickery so make sure we see it 3 times.
                    for (int i = 0; i < 3; i++)
                        await ReadUntilChanged(BattleMenuOffset, BattleMenuReady, 5_000, 0_100, true, token).ConfigureAwait(false);
                    Log("Unwanted encounter started, running away...");
                    await FleeToOverworld(token).ConfigureAwait(false);
                    // Extra delay to be sure we're fully out of the battle.
                    await Task.Delay(0_250, token).ConfigureAwait(false);
                }

                List<PK8> PK8s = await ReadOwPokemonFromBlock(KCoordinates, sav, token).ConfigureAwait(false);
                if (PK8s.Count > 0)
                {
                    foreach (PK8 pkm in PK8s)
                    {
                        Log($"{(Species)pkm.Species}");
                        if (await HandleEncounter(pkm, IsPKLegendary(pkm.Species), token).ConfigureAwait(false))
                        {
                            //Save the game to update KCoordinates block
                            await Click(X, 2_000, token).ConfigureAwait(false);
                            await Click(R, 2_000, token).ConfigureAwait(false);
                            await Click(A, 5_000, token).ConfigureAwait(false);
                            return;
                        }
                    }
                }
                else
                    Log("Empty list, no overworld data in KCoordinates!");

                //Walk to despawn and respawn pokemons
                await ResetStick(token).ConfigureAwait(false);
                await SetStick(LEFT, 0, -30_000, 5_000, token).ConfigureAwait(false);
                await ResetStick(token).ConfigureAwait(false);
                await SetStick(LEFT, 0, 30_000, 7_000, token).ConfigureAwait(false);
                await ResetStick(token).ConfigureAwait(false);

                //Check again if encountered an unwanted pokemon
                if (await IsInBattle(token).ConfigureAwait(false))
                {
                    // Offsets are flickery so make sure we see it 3 times.
                    for (int i = 0; i < 3; i++)
                        await ReadUntilChanged(BattleMenuOffset, BattleMenuReady, 5_000, 0_100, true, token).ConfigureAwait(false);
                    Log("Unwanted encounter started, running away...");
                    await FleeToOverworld(token).ConfigureAwait(false);
                    // Extra delay to be sure we're fully out of the battle.
                    await Task.Delay(0_250, token).ConfigureAwait(false);
                }

                //Save the game to update KCoordinates block
                await Click(X, 2_000, token).ConfigureAwait(false);
                await Click(R, 2_000, token).ConfigureAwait(false);
                await Click(A, 5_000, token).ConfigureAwait(false);

                Log("Game saved, seed rerolled.");
            }
        }
        private async Task DoSeededEncounter(CancellationToken token, EncounterType type)
        {
            SAV8 sav = await GetFakeTrainerSAV(token).ConfigureAwait(false);
            Species dexn;
            uint offset = 0x00;
            if (type == (EncounterType)7)
            {
                dexn = (Species)144;
                offset = CrownTundraSnowslideSlopeSpawns;
            }
            else if (type == (EncounterType)8)
            {
                dexn = (Species)145;
                offset = WildAreaMotostokeSpawns;
            }
            else if (type == (EncounterType)9)
            {
                dexn = (Species)146;
                offset = IsleOfArmorStationSpaws;
            }
            else if (type == (EncounterType)10)
            {
                dexn = (Species)321;
                offset = IsleOfArmorStationSpaws;
            }
            else
                dexn = Hub.Config.StopConditions.StopOnSpecies;

            while (!token.IsCancellationRequested && offset != 0)
            {
                var pkm = await ReadOwPokemon(dexn, offset, null, sav, token).ConfigureAwait(false);
                if (pkm != null && await HandleEncounter(pkm, IsPKLegendary(pkm.Species), token).ConfigureAwait(false))
                {
                    await Click(X, 3_500, token).ConfigureAwait(false);
                    await Click(R, 5_000, token).ConfigureAwait(false);
                    await Click(X, 0_100, token).ConfigureAwait(false);
                    Log($"The overworld encounter has been found. The progresses has been saved and the game is paused, you can now go and catch {SpeciesName.GetSpeciesName((int)dexn, 2)}");
                    return;
                }
                else
                    await FlyToRerollSeed(type, token).ConfigureAwait(false);
            }
        }
        private async Task<bool> HandleEncounter(PK8 pk, bool legends, CancellationToken token)
        {
            encounterCount++;

            //Star/Square Shiny Recognition
            var showdowntext = ShowdownParsing.GetShowdownText(pk);
            if (pk.IsShiny && pk.ShinyXor == 0)
                showdowntext = showdowntext.Replace("Shiny: Yes", "Shiny: Square");
            else if (pk.IsShiny)
                showdowntext = showdowntext.Replace("Shiny: Yes", "Shiny: Star");

            Log($"Encounter: {encounterCount}{Environment.NewLine}{showdowntext}{Environment.NewLine}{GetRibbonsList(pk)}{Environment.NewLine}");
            if (legends)
                Counts.AddCompletedLegends();
            else
                Counts.AddCompletedEncounters();

            if (DumpSetting.Dump && !string.IsNullOrEmpty(DumpSetting.DumpFolder))
                DumpPokemon(DumpSetting.DumpFolder, legends ? "legends" : "encounters", pk);

            if (StopConditionSettings.EncounterFound(pk, DesiredIVs, Hub.Config.StopConditions))
            {
                if (!String.IsNullOrEmpty(Hub.Config.Discord.UserTag))
                    Log($"<@{Hub.Config.Discord.UserTag}> result found! Stopping routine execution; restart the bot(s) to search again.");
                else
                    Log("Result found! Stopping routine execution; restart the bot(s) to search again.");
                if (Hub.Config.StopConditions.CaptureVideoClip)
                {
                    await Task.Delay(Hub.Config.StopConditions.ExtraTimeWaitCaptureVideo, token).ConfigureAwait(false);
                    await PressAndHold(CAPTURE, 2_000, 1_000, token).ConfigureAwait(false);
                }
                return true;
            }
            return false;
        }

        private string GetRibbonsList(PK8 pk)
        {
            string ribbonsList = "";
            for (var mark = MarkIndex.MarkLunchtime; mark <= MarkIndex.MarkSlump; mark++)
                if (pk.GetRibbon((int)mark))
                    ribbonsList += mark;

            return ribbonsList;
        }

        private async Task ResetStick(CancellationToken token)
        {
            // If aborting the sequence, we might have the stick set at some position. Clear it just in case.
            await SetStick(LEFT, 0, 0, 0_500, token).ConfigureAwait(false); // reset
        }

        private async Task FleeToOverworld(CancellationToken token)
        {
            // This routine will always escape a battle.
            await Click(DUP, 0_200, token).ConfigureAwait(false);
            await Click(A, 1_000, token).ConfigureAwait(false);

            while (await IsInBattle(token).ConfigureAwait(false))
            {
                await Click(B, 0_500, token).ConfigureAwait(false);
                await Click(B, 1_000, token).ConfigureAwait(false);
                await Click(DUP, 0_200, token).ConfigureAwait(false);
                await Click(A, 1_000, token).ConfigureAwait(false);
            }
        }
    }
}