﻿///************************************************************************
//Project Lorule: A Dark Ages Server (http://darkages.creatorlink.net/index/)
//Copyright(C) 2018 TrippyInc Pty Ltd
//
//This program is free software: you can redistribute it and/or modify
//it under the terms of the GNU General Public License as published by
//the Free Software Foundation, either version 3 of the License, or
//(at your option) any later version.
//
//This program is distributed in the hope that it will be useful,
//but WITHOUT ANY WARRANTY; without even the implied warranty of
//MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
//GNU General Public License for more details.
//
//You should have received a copy of the GNU General Public License
//along with this program.If not, see<http://www.gnu.org/licenses/>.
//*************************************************************************/
using Darkages.Common;
using Darkages.Network.ServerFormats;
using Darkages.Scripting;
using Darkages.Storage;
using Darkages.Storage.locales.Scripts.Global;
using Darkages.Types;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Darkages.Network.Game
{
    public class GameClient : NetworkClient<GameClient>
    {
        public Collection<GlobalScript> GlobalScripts = new Collection<GlobalScript>();

        public GameServer Server { get; set; }

        public DateTime LastPing { get; set; }

        public DateTime LastSave { get; set; }

        public DateTime LastClientRefresh { get; set; }

        public bool IsRefreshing =>
            DateTime.Now - LastClientRefresh < new TimeSpan(0, 0, 0, 0, ServerContext.Config.RefreshRate);

        public GameServerTimer HpRegenTimer { get; set; }

        public GameServerTimer MpRegenTimer { get; set; }

        public Aisling Aisling { get; set; }

        public bool ShouldUpdateMap { get; set; }

        public DateTime LastMessageSent { get; set; }

        public DateTime LastPingResponse { get; set; }

        public byte LastActivatedLost { get; set; }

        public DialogSession DlgSession { get; set; }

        public DateTime BoardOpened { get; set; }

        public DateTime LastWhisperMessageSent { get; set; }

        public ushort LastBoardActivated { get; set; }

        public bool IsDead()
        {
            return Aisling != null && Aisling.Flags.HasFlag(AislingFlags.Dead);
        }

        public bool CanSeeGhosts()
        {
            return IsDead();
        }

        public bool CanSeeHidden()
        {
            return Aisling != null && (Aisling.Flags & AislingFlags.SeeInvisible) == AislingFlags.SeeInvisible;
        }

        public void WarpTo(WarpTemplate warps)
        {
            if (warps.WarpType == WarpType.World)
                return;
              
            if (ServerContext.GlobalMapCache.Values.Any(i => i.ID == warps.ActivationMapId))
            {
                if (warps.LevelRequired > 0 && Aisling.ExpLevel < warps.LevelRequired)
                {
                    var msgTier = Math.Abs(Aisling.ExpLevel - warps.LevelRequired);

                    SendMessage(0x02, msgTier <= 10 
                        ? string.Format("You can't enter there just yet. ({0} req)", warps.LevelRequired) 
                        : string.Format("Nightmarish visions of your own death repel you. ({0} Req)", warps.LevelRequired));
                    return;
                }

                if (Aisling.Map.ID != warps.To.AreaID)
                {
                    TransitionToMap(warps.To.AreaID, warps.To.Location);
                }
                else
                {
                    LeaveArea(true);
                    Aisling.X = warps.To.Location.X;
                    Aisling.Y = warps.To.Location.Y;
                    EnterArea();
                    Aisling.Client.CloseDialog();
                }
            }
        }

        public void LearnSkill(Mundane Source, SkillTemplate subject, string message)
        {
            if (PayPrerequisites(subject.Prerequisites))
            {
                Skill.GiveTo(this, subject.Name);
                SendOptionsDialog(Source, message);

                Aisling.Show(Scope.NearbyAislings,
                    new ServerFormat29((uint)Aisling.Serial, (uint)Source.Serial,
                    subject?.TargetAnimation ?? 124,
                    subject?.TargetAnimation ?? 124, 100));
            }
        }

        public void LearnSpell(Mundane Source, SpellTemplate subject, string message)
        {
            if (PayPrerequisites(subject.Prerequisites))
            {
                Spell.GiveTo(this, subject.Name);
                SendOptionsDialog(Source, message);

                Aisling.Show(Scope.NearbyAislings,
                    new ServerFormat29((uint)Aisling.Serial, (uint)Source.Serial,
                    subject?.TargetAnimation ?? 124,
                    subject?.TargetAnimation ?? 124, 100));
            }
        }

        public bool PayPrerequisites(LearningPredicate prerequisites)
        {
            if (prerequisites == null)
            {
                return false;
            }



            PayItemPrerequisites(prerequisites);
            {
                if (prerequisites.Gold_Required > 0)
                {
                    Aisling.GoldPoints -= prerequisites.Gold_Required;
                    if (Aisling.GoldPoints <= 0)
                        Aisling.GoldPoints = 0;
                }
                SendStats(StatusFlags.All);
                return true;
            }
        }

        private void PayItemPrerequisites(LearningPredicate prerequisites)
        {
            if (prerequisites.Items_Required != null && prerequisites.Items_Required.Count > 0)
            {
                foreach (var retainer in prerequisites.Items_Required)
                {
                    var item = Aisling.Inventory.Get(i => i.Template.Name == retainer.Item);

                    foreach (var i in item)
                    {
                        if (!i.Template.Flags.HasFlag(ItemFlags.Stackable))
                        {
                            Aisling.EquipmentManager.RemoveFromInventory(i, i.Template.CarryWeight > 0);
                            break;
                        }
                        else
                        {
                            Aisling.Inventory.RemoveRange(Aisling.Client, i, retainer.AmountRequired);
                            break;
                        }
                    }
                }
            }
        }

        public void TransitionToMap(Area area, Position position)
        {
            if (area == null)
                return;

            if (area.ID != Aisling.CurrentMapId)
            {
                LeaveArea(true);
                Aisling.X = position.X;
                Aisling.Y = position.Y;
                Aisling.CurrentMapId = area.ID;
                EnterArea();
            }
            else
            {
                LeaveArea(true, false);
                Aisling.X = position.X;
                Aisling.Y = position.Y;
                EnterArea();
            }

            Aisling.Client.CloseDialog();
        }

        public void TransitionToMap(int area, Position position)
        {
            if (ServerContext.GlobalMapCache.ContainsKey(area))
            {
                var target = ServerContext.GlobalMapCache[area];
                if (target != null)
                {
                    TransitionToMap(target, position);
                }
            }
        }

        public void CloseDialog()
        {
            SendPacket(new byte[] { 0x30, 0x00, 0x0A, 0x00 });
        }

        public void Update(TimeSpan elapsedTime)
        {
            #region Sanity Checks

            if (Aisling == null)
                return;

            if (!Aisling.LoggedIn)
                return;

            if ((DateTime.UtcNow - Aisling.LastLogged).TotalMilliseconds < ServerContext.Config.LingerState)
                return;

            //if ((DateTime.UtcNow - LastPingResponse).TotalSeconds > ServerContext.Config.TimeOutValue)
            //{
            //    Server.ClientDisconnected(this);
            //    return;
            //}

            #endregion

            try
            {
                StatusCheck();
                Regeneration(elapsedTime);
                UpdateStatusBar(elapsedTime);
                UpdateGlobalScripts(elapsedTime);
                HandleTimeOuts();
            }
            catch (Exception err)
            {
                logger.Error(err, "Fatal Exception: Client Update.");
            }
        }

        private void StatusCheck()
        {
            if (Aisling.Flags.HasFlag(AislingFlags.Dead) 
                && (Aisling.Debuffs.Count > 0 || Aisling.Buffs.Count > 0))
            {
                Aisling.RemoveBuffsAndDebuffs();
            }
        }

        private void HandleTimeOuts()
        {
            try
            {
                if (Aisling.Exchange != null)
                {
                    if (Aisling.Exchange.Trader != null)
                    {
                        if (!Aisling.Exchange.Trader.LoggedIn
                        || !Aisling.WithinRangeOf(Aisling.Exchange.Trader))
                        {
                            Aisling.CancelExchange();
                        }
                    }
                }

                if (Aisling.PortalSession == null)
                    return;

                if (Aisling.PortalSession.IsMapOpen)
                {
                    if ((DateTime.UtcNow - Aisling.PortalSession.DateOpened).TotalSeconds > ServerContext.Config.PortalTimeOut)
                    {
                        Aisling.GoHome();
                        Aisling.PortalSession = null;
                    }
                }
            }
            catch (Exception error)
            {
                logger.Error(error, "Error: HandleTimeOuts");
            }
        }

        private void UpdateStatusBar(TimeSpan elapsedTime)
        {
            try
            {
                Aisling.UpdateBuffs(elapsedTime);
                Aisling.UpdateDebuffs(elapsedTime);
            }
            catch (Exception error)
            {
                logger.Error(error, "Error: UpdateStatusBar");
            }
        }

        private void UpdateGlobalScripts(TimeSpan elapsedTime)
        {
            try
            {
                foreach (var globalscript in GlobalScripts)
                    globalscript?.Update(elapsedTime);
            }
            catch (Exception error)
            {
                logger.Error(error, "Error: Update Global Scripts");
            }
        }

        private void Regeneration(TimeSpan elapsedTime)
        {
            try
            {
                if (HpRegenTimer == null)
                    return;

                if (MpRegenTimer == null)
                    return;

                var hpChanged = false;
                var mpChanged = false;

                if (Aisling.CurrentHp <= 0)
                {
                    Aisling.CurrentHp = 0;
                    hpChanged = true;
                }


                HpRegenTimer.Update(elapsedTime);
                MpRegenTimer.Update(elapsedTime);

                #region Hp Regen

                if (HpRegenTimer.Elapsed)
                {
                    HpRegenTimer.Reset();

                    if (!HpRegenTimer.Disabled && Aisling.LoggedIn)
                    {
                        if (Aisling.CurrentHp < Aisling.MaximumHp)
                        {
                            hpChanged = true;

                            var hpRegenSeed = (Math.Abs(Aisling.Con - Aisling.ExpLevel)).Clamp(0, 10) * 0.01;
                            var hpRegenAmount = Aisling.MaximumHp * (hpRegenSeed + 0.10);


                            Aisling.CurrentHp = (Aisling.CurrentHp + (int)hpRegenAmount).Clamp(0, Aisling.MaximumHp);
                        }
                    }
                }

                #endregion

                #region Mp Regen

                if (MpRegenTimer.Elapsed)
                {
                    MpRegenTimer.Reset();
                    if (!MpRegenTimer.Disabled && Aisling.LoggedIn)
                    {
                        if (Aisling.CurrentMp < Aisling.MaximumMp)
                        {
                            mpChanged = true;

                            var mpRegenSeed = (Math.Abs(Aisling.Wis - Aisling.ExpLevel)).Clamp(0, 10) * 0.01;
                            var mpRegenAmount = Aisling.MaximumMp * (mpRegenSeed + 0.10);

                            Aisling.CurrentMp = (Aisling.CurrentMp + (int)mpRegenAmount).Clamp(0, Aisling.MaximumMp);
                        }
                    }
                }

                #endregion

                if (!IsDead())
                {
                    if (!Aisling.LoggedIn)
                        return;

                    if (hpChanged || mpChanged)
                        Send(new ServerFormat08(Aisling, StatusFlags.StructB));
                }
            }
            catch (Exception error)
            {
                logger.Error(error, "Error: Regeneration");
            }
        }

        public bool Load()
        {
            if (Aisling == null || Aisling.AreaID == 0)
                return false;
            if (!ServerContext.GlobalMapCache.ContainsKey(Aisling.AreaID))
                return false;

            SetAislingStartupVariables();

            try
            {
                LoadGlobalScripts();
                Thread.Sleep(50);
                InitSpellBar();
                Thread.Sleep(50);
                SetupRegenTimers();
                Thread.Sleep(50);
                LoadInventory();
                Thread.Sleep(50);
                LoadSkillBook();
                Thread.Sleep(100);
                LoadSpellBook();
                Thread.Sleep(100);
                LoadEquipment();
                Thread.Sleep(100);
                SendProfileUpdate();
                Thread.Sleep(100);

                SendStats(StatusFlags.All);
                Thread.Sleep(100);
            }
            catch
            {
                return false;
            }

            return true;
        }

        private void SetAislingStartupVariables()
        {
            LastSave = DateTime.UtcNow;
            LastPingResponse = DateTime.UtcNow;
            BoardOpened = DateTime.UtcNow;
            {
                Aisling.BonusAc = ServerContext.Config.BaseAC;
                Aisling.Exchange = null;
                Aisling.PortalSession = null;
                Aisling.LastMapId = short.MaxValue;
            }
        }

        private void LoadGlobalScripts()
        {
            GlobalScripts.Add(ScriptManager.Load<GrimReaper>("Grim Reaper", this));
            GlobalScripts.Add(ScriptManager.Load<Tutorial>("Tutorial", this));
            GlobalScripts.Add(ScriptManager.Load<TowerDefenders>("Tower Defender Player Reaper", this));
            GlobalScripts.Add(ScriptManager.Load<Reactors>("Reactors", this));

        }

        private void SetupRegenTimers()
        {
            var HpregenRate = ServerContext.Config.RegenRate;
            var MpregenRate = ServerContext.Config.RegenRate / 2;

            HpRegenTimer = new GameServerTimer(
                TimeSpan.FromMilliseconds(HpregenRate));
            MpRegenTimer = new GameServerTimer(
                TimeSpan.FromMilliseconds(MpregenRate));
        }

        private void InitSpellBar()
        {
            foreach (var buff in Aisling.Buffs.Select(i => i.Value))
            {
                buff.OnApplied(Aisling, buff);
                {
                    buff.Display(Aisling);
                }
            }

            foreach (var debuff in Aisling.Debuffs.Select(i => i.Value))
            {
                debuff.OnApplied(Aisling, debuff);
                {
                    debuff.Display(Aisling);
                }
            }
        }

        private void LoadEquipment()
        {
            var formats = new List<NetworkFormat>();

            foreach (var item in Aisling.EquipmentManager.Equipment)
            {
                var equipment = item.Value;

                if (equipment == null || equipment.Item == null || equipment.Item.Template == null)
                    continue;

                if (equipment.Item.Template != null)
                {
                    if (ServerContext.GlobalItemTemplateCache.ContainsKey(equipment.Item.Template.Name))
                    {
                        var template = ServerContext.GlobalItemTemplateCache[equipment.Item.Template.Name];
                        {
                            item.Value.Item.Template = template;

                            if (item.Value.Item.Upgrades > 0)
                                Item.ApplyQuality(item.Value.Item);
                        }
                    }
                }


                equipment.Item.Script = ScriptManager.Load<ItemScript>(equipment.Item.Template.ScriptName, equipment.Item);
                equipment.Item.Script?.Equipped(Aisling, (byte)equipment.Slot);

                if (equipment.Item.CanCarry(Aisling))
                {
                    //apply weight to items that are equipped.
                    Aisling.CurrentWeight += equipment.Item.Template.CarryWeight;

                    formats.Add(new ServerFormat37(equipment.Item, (byte)equipment.Slot));
                }
                //for some reason, Aisling is out of Weight!
                else
                {
                    //clone and release item
                    var nitem = Clone<Item>(item.Value.Item);
                    nitem.Release(Aisling, Aisling.Position);

                    //display message
                    SendMessage(0x02, string.Format("{0} is too heavy to hold.", nitem.Template.Name));

                    continue;
                }

                if ((equipment.Item.Template.Flags & ItemFlags.Equipable) == ItemFlags.Equipable)
                    for (var i = 0; i < Aisling.SpellBook.Spells.Count; i++)
                    {
                        var spell = Aisling.SpellBook.FindInSlot(i);
                        if (spell != null && spell.Template != null)
                            equipment.Item.UpdateSpellSlot(this, spell.Slot);
                    }
            }

            foreach (var format in formats)
                Aisling.Client.Send(format);
        }

        private void LoadSpellBook()
        {
            var spells_Available = Aisling.SpellBook.Spells.Values
                .Where(i => i != null && i.Template != null).ToArray();

            var formats = new List<NetworkFormat>();

            for (var i = 0; i < spells_Available.Length; i++)
            {
                var spell = spells_Available[i];

                if (spell.Template != null)
                {
                    if (ServerContext.GlobalSpellTemplateCache.ContainsKey(spell.Template.Name))
                    {
                        var template = ServerContext.GlobalSpellTemplateCache[spell.Template.Name];
                        {
                            spell.Template = template;
                        }
                    }
                }

                spell.InUse = false;
                spell.NextAvailableUse = DateTime.UtcNow;

                spell.Lines = spell.Template.BaseLines;
                spell.Script = ScriptManager.Load<SpellScript>(spell.Template.ScriptKey, spell);
                Aisling.SpellBook.Set(spell, false);

                formats.Add(new ServerFormat17(spell));
            }

            foreach (var format in formats)
                Aisling.Client.Send(format);
        }

        private void LoadSkillBook()
        {
            var skills_Available = Aisling.SkillBook.Skills.Values
                .Where(i => i != null && i.Template != null).ToArray();

            var formats = new List<NetworkFormat>();

            for (var i = 0; i < skills_Available.Length; i++)
            {
                var skill = skills_Available[i];

                if (skill.Template != null)
                {
                    if (ServerContext.GlobalSkillTemplateCache.ContainsKey(skill.Template.Name))
                    {
                        var template = ServerContext.GlobalSkillTemplateCache[skill.Template.Name];
                        {
                            skill.Template = template;
                        }
                    }
                }

                skill.InUse = false;
                skill.NextAvailableUse = DateTime.UtcNow;

                formats.Add(new ServerFormat2C(skill.Slot,
                    skill.Icon,
                    skill.Name));


                skill.Script = ScriptManager.Load<SkillScript>(skill.Template.ScriptName, skill);
                Aisling.SkillBook.Set(skill, false);
            }

            foreach (var format in formats)
                Aisling.Client.Send(format);
        }

        private void LoadInventory()
        {
            var items_Available = Aisling.Inventory.Items.Values
                .Where(i => i != null && i.Template != null).ToArray();

            var formats = new List<NetworkFormat>();

            for (var i = 0; i < items_Available.Length; i++)
            {
                var item = items_Available[i];
                {
                    item.Script = ScriptManager.Load<ItemScript>(item.Template.ScriptName, item);
                }

                if (string.IsNullOrEmpty(item.Template.Name))
                    continue;

                if (ServerContext.GlobalItemTemplateCache.ContainsKey(item.Template.Name))
                {
                    var template = ServerContext.GlobalItemTemplateCache[item.Template.Name];
                    {
                        item.Template = template;
                    }

                    if (item.Upgrades > 0)
                        Item.ApplyQuality(item);
                }

                if (item.Template != null)
                {


                    if (Aisling.CurrentWeight + item.Template.CarryWeight < Aisling.MaximumWeight)
                    {
                        var format = new ServerFormat0F(item);
                        formats.Add(format);
                        Aisling.Inventory.Set(item, false);
                        Aisling.CurrentWeight += item.Template.CarryWeight;
                    }
                    //for some reason, Aisling is out of Weight!
                    else
                    {
                        //clone and release item
                        var copy = Clone<Item>(item);
                        {
                            copy.Release(Aisling, Aisling.Position);

                            //display message
                            SendMessage(0x02, string.Format("You stumble and drop {0}", item.Template.Name));
                        }
                    }
                }
            }

            if (formats.Count > 0)
                formats.ForEach(i => Send(i));
        }

        public void UpdateDisplay()
        {
            //construct display Format for dispatching out.
            var response = new ServerFormat33(this, Aisling);

            //Display Aisling to self.
            Aisling.Show(Scope.Self, response);

            //Display Aisling to everyone else nearby.
            if (Aisling.Flags.HasFlag(AislingFlags.Dead))
            {
                //only show to clients who can see ghosts.
                var nearby = GetObjects<Aisling>(i => i.WithinRangeOf(Aisling) && i.Client.CanSeeGhosts());
                Aisling.Show(Scope.NearbyAislingsExludingSelf, response, nearby);
            }
            else
            {
                //show to everyone except myself.
                Aisling.Show(Scope.NearbyAislingsExludingSelf, response);
            }
        }

        public void Refresh(bool delete = false)
        {
            LeaveArea(delete);
            EnterArea();
        }

        public void LeaveArea(bool update = false, bool delete = false)
        {
            if (Aisling.LastMapId == short.MaxValue)
            {
                Aisling.LastMapId = Aisling.CurrentMapId;
            }

            Aisling.Remove(update, delete);
        }

        public void EnterArea()
        {
            SendSerial();
            Insert();
            RefreshMap();
            SendLocation();
            UpdateDisplay();
            RefreshObjects();
        }

        public void SendMusic()
        {
            Aisling.Client.SendPacket(new byte[]
            {
                0x19, 0x00, 0xFF,
                (byte) Aisling.Map.Music
            });
        }

        public void SendSound(byte sound, Scope scope = Scope.Self)
        {
            var empty = new ServerFormat13
            {
                Serial = Aisling.Serial,
                Health = byte.MaxValue,
                Sound = sound
            };

            Aisling.Show(scope, empty);
        }

        public void Insert()
        {
            if (!Aisling.Map.Ready)
                return;

            if (GetObject<Aisling>(i => i.Serial == Aisling.Serial) == null)
                AddObject(Aisling);

            Aisling.Map.Update(Aisling.X, Aisling.Y, TileContent.Aisling);
        }

        public void RefreshMap()
        {
            if (Aisling.CurrentMapId != Aisling.LastMapId)
            {
                ShouldUpdateMap = true;
                Aisling.LastMapId = Aisling.CurrentMapId;
                SendMusic();
            }


            if (ShouldUpdateMap)
            {
                Aisling.ViewFrustrum.Clear();
                Send(new ServerFormat15(Aisling.Map));
            }
        }

        public void RefreshObjects()
        {
            var nearbyobjs = GetObjects(i => i.WithinRangeOf(Aisling), Get.All);
            foreach (var obj in nearbyobjs)
            {
                if (obj is Aisling)
                    continue;

                if (Aisling.View(obj))
                {
                    obj.ShowTo(Aisling);
                }

            }
        }

        private void SendSerial()
        {
            //send Serial
            Send(new ServerFormat05(Aisling));
        }

        public void SendLocation()
        {
            //send position
            Send(new ServerFormat04(Aisling));
        }

        public void Save()
        {
            //if (Aisling.AreaID != Aisling.CurrentMapId)
            //    Aisling.AreaID = Aisling.CurrentMapId;

            ThreadPool.QueueUserWorkItem((state) =>
            {
                StorageManager.AislingBucket.Save(Aisling);
                {
                    LastSave = DateTime.UtcNow;
                }
            });
        }

        public void SendMessage(byte type, string text)
        {
            Send(new ServerFormat0A(type, text));
            LastMessageSent = DateTime.UtcNow;
        }

        public void SendMessage(Scope scope, byte type, string text)
        {
            switch (scope)
            {
                case Scope.Self:
                    SendMessage(type, text);
                    break;
                case Scope.NearbyAislings:
                    {
                        var nearby = GetObjects<Aisling>(i => i.WithinRangeOf(Aisling));

                        foreach (var obj in nearby)
                            obj.Client.SendMessage(type, text);
                    }
                    break;
                case Scope.NearbyAislingsExludingSelf:
                    {
                        var nearby = GetObjects<Aisling>(i => i.WithinRangeOf(Aisling));

                        foreach (var obj in nearby)
                        {
                            if (obj.Serial == Aisling.Serial)
                                continue;

                            obj.Client.SendMessage(type, text);
                        }
                    }
                    break;
                case Scope.AislingsOnSameMap:
                    {
                        var nearby = GetObjects<Aisling>(i => i.WithinRangeOf(Aisling)
                                                              && i.CurrentMapId == Aisling.CurrentMapId);

                        foreach (var obj in nearby)
                            obj.Client.SendMessage(type, text);
                    }
                    break;
                case Scope.All:
                    {
                        var nearby = GetObjects<Aisling>(i => i.LoggedIn);
                        foreach (var obj in nearby)
                            obj.Client.SendMessage(type, text);
                    }
                    break;
            }
        }

        public void Say(string message, byte type = 0x00)
        {
            var response = new ServerFormat0D
            {
                Serial = Aisling.Serial,
                Type = type,
                Text = message
            };

            Aisling.Show(Scope.NearbyAislings, response);
        }

        public void SendAnimation(ushort Animation, Sprite To, Sprite From, byte speed = 100, bool repeat = false)
        {
            var format = new ServerFormat29((uint)From.Serial, (uint)To.Serial, Animation, 0, speed);

            if (!repeat)
            {
                Aisling.Show(Scope.NearbyAislings, format);
                return;
            }

            new TaskFactory().StartNew(() =>
            {
                while (ServerContext.Running)
                {
                    if (To == null)
                        break;

                    if (From == null)
                        break;

                    if (!Aisling.WithinRangeOf(To, 6))
                        break;


                    To?.Show(Scope.NearbyAislings, format);

                    Thread.Sleep(1000);
                }
            });
        }

        public void SendItemShopDialog(Mundane mundane, string text, ushort step, IEnumerable<ItemTemplate> items)
        {
            Send(new ServerFormat2F(mundane, text, new ItemShopData(step, items)));
        }

        public void SendItemSellDialog(Mundane mundane, string text, ushort step, IEnumerable<byte> items)
        {
            Send(new ServerFormat2F(mundane, text, new ItemSellData(step, items)));
        }

        public void SendOptionsDialog(Mundane mundane, string text, params OptionsDataItem[] options)
        {
            Send(new ServerFormat2F(mundane, text, new OptionsData(options)));
        }

        public void SendOptionsDialog(Mundane mundane, string text, string args, params OptionsDataItem[] options)
        {
            Send(new ServerFormat2F(mundane, text, new OptionsPlusArgsData(options, args)));
        }

        public void SendSkillLearnDialog(Mundane mundane, string text, ushort step, IEnumerable<SkillTemplate> skills)
        {
            Send(new ServerFormat2F(mundane, text, new SkillAcquireData(step, skills)));
        }

        public void SendSpellLearnDialog(Mundane mundane, string text, ushort step, IEnumerable<SpellTemplate> spells)
        {
            Send(new ServerFormat2F(mundane, text, new SpellAcquireData(step, spells)));
        }

        public void SendSkillForgetDialog(Mundane mundane, string text, ushort step)
        {
            Send(new ServerFormat2F(mundane, text, new SkillForfeitData(step)));
        }

        public void SendSpellForgetDialog(Mundane mundane, string text, ushort step)
        {
            Send(new ServerFormat2F(mundane, text, new SpellForfeitData(step)));
        }

        public void SendStats(StatusFlags flags)
        {
            Send(new ServerFormat08(Aisling, flags));
        }

        public void SendProfileUpdate()
        {
            SendPacket(new byte[] { 73, 0x00 });
        }

        public void TrainSpell(Spell spell)
        {
            if (spell.Level < spell.Template.MaxLevel)
            {
                var toImprove = (int)(0.10 / spell.Template.LevelRate);
                if (spell.Casts++ >= toImprove)
                {
                    spell.Level++;
                    spell.Casts = 0;
                    Send(new ServerFormat17(spell));
                    SendMessage(0x02, string.Format("{0} has improved.", spell.Template.Name));
                }
            }
        }

        public void TrainSkill(Skill skill)
        {
            if (skill.Level < skill.Template.MaxLevel)
            {

                var toImprove = (int)(0.10 / skill.Template.LevelRate);
                if (skill.Uses++ >= toImprove)
                {
                    skill.Level++;
                    skill.Uses = 0;
                    Send(new ServerFormat2C(skill.Slot, skill.Icon, skill.Name));

                    SendMessage(0x02, string.Format("{0} has improved. (Lv. {1})",
                        skill.Template.Name,
                        skill.Level));
                }
            }

            Send(new ServerFormat3F((byte)skill.Template.Pane,
                skill.Slot,
                skill.Template.Cooldown));
        }

        /// <summary>
        ///     Stop and Interupt everything this client is doing.
        /// </summary>
        public void Interupt()
        {
            GameServer.CancelIfCasting(this);
            SendLocation();
        }

        public bool Revive()
        {
            Aisling.CurrentHp = Aisling.MaximumHp / 6;
            Aisling.Flags = AislingFlags.Normal;
            HpRegenTimer.Disabled = false;
            MpRegenTimer.Disabled = false;

            SendStats(StatusFlags.All);

            return true;
        }
    
        public bool CheckReqs(GameClient client, Item item)
        {
            var message = string.Empty;

            if (client.Aisling.ExpLevel <= item.Template.LevelRequired)
            {
                message = "You can't wear this yet.";
                if (message != string.Empty)
                {
                    client.SendMessage(0x02, message);
                    return false;
                }
            }

            if (item.Durability <= 0)
            {
                message = "You can't wear something to fucked. go repair it first.";
                if (message != string.Empty)
                {
                    client.SendMessage(0x02, message);
                    return false;
                }
            }

            if (client.Aisling.Path != item.Template.Class && !(item.Template.Class == Class.Peasant))
            {
                if (client.Aisling.ExpLevel >= item.Template.LevelRequired)
                {
                    message = "This is best suited for somebody else.";
                }
                else
                {
                    message = "You can't wear this yet.";
                }
            }

            if (message != string.Empty)
            {
                client.SendMessage(0x02, message);
                return false;
            }


            if (client.Aisling.ExpLevel >= item.Template.LevelRequired
                && (client.Aisling.Path == item.Template.Class || item.Template.Class == Class.Peasant))
            {
                if (item.Template.Gender == Gender.Both)
                {
                    client.Aisling.EquipmentManager.Add(item.Template.EquipmentSlot, item);
                }
                else
                {
                    if (item.Template.Gender == client.Aisling.Gender)
                    {
                        client.Aisling.EquipmentManager.Add(item.Template.EquipmentSlot, item);
                    }
                    else
                    {
                        client.SendMessage(0x02, ServerContext.Config.DoesNotFitMessage);
                        return false;
                    }
                }

                return true;
            }

            client.SendMessage(0x02, ServerContext.Config.CantEquipThatMessage);
            return false;
        }
    }
}
