﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Harmony;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using SplitScreen.Patchers;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;


/*BREAKDOWN OF MOD:
 *	- Game1.game1.InactiveSleepTime = 0 : no fps throttling when window inactive
 *	- Program.releaseBuild = false : prevents updateActiveMenu returning immidiately(also enables cheats but haven't check what that means yet...)
 *	- Every update tick, (SInputState)Game1.input.RealController is set to GamePad.GetState(). Mouse is set to whatever is in FakeMouse (unless window is focused) This prevents suppression when window inactive
 *	- UpdateControlInput is called when it should according to SDV's Game1 if statement logic, except ONLY when InActive
 *	- XNA.Framework.Mouse.SetPosition is overwritten to only update FAKE mouse when unfocused
 *	- Game1.getMousePosition() is overwritten return FAKE mouse when unfocused
 */

/*Loop works in this order:
 * - SMAPI Updates itself, including polling the input
 * - SMAPI calls Game1.Update
 * - GameEvents.UpdateTick is called
 * 
 * If there is a window active, Game1.Update is called BEFORE we mod the input, so there is no effect.
 * If inactive, we mod the input, then call UpdateControlInput
	 */

namespace SplitScreen
{
	public class ModEntry : Mod
	{
		private InputState sInputState;

		private const int secondsBetweenWarnings = 5;

		private PlayerIndexController playerIndexController;
				
		public override void Entry(IModHelper helper)
		{
			//Get player index if it is set in launch options, e.g. SMAPI.exe --log-path "third.txt" --player-index 3
			this.playerIndexController = new PlayerIndexController(Monitor, Environment.GetCommandLineArgs());

			//Removes FPS throttle when window inactive
			Game1.game1.InactiveSleepTime = new TimeSpan(0);

			/* When Program.releaseBuild = false, LeftStick and RightStick open a performance dialog and the chat window, respectively
			   So it is a good idea to unbind LeftStick and RightStick in x360ce */
			/* Prevents not continuing with update threads when InActive 
			  Also calls updateActiveMenu when window is inactive so we don't have to */
			Program.releaseBuild = false;

			//SMAPI sets Game1.input to an instance of SInputState
			sInputState = (Helper.Reflection.GetField<InputState>(typeof(Game1), "input", true)).GetValue();

			//Manually update the RealController and RealMouse fields of SMAPI's SInputeState (which derives from SDV's InputState but is internal and sealed, so needs reflection)
			//Also manually call Game1.UpdateControlInput
			StardewModdingAPI.Events.GameEvents.UpdateTick += OnUpdateTick;

			//Used to update GamePad/Mouse when in shipping menu (before save)
			StardewModdingAPI.Events.SpecialisedEvents.UnvalidatedUpdateTick += OnUnvalidatedUpdateTick;

			//Used to send warnings periodically if windows are set up incorrectly
			int secondsPassed = 0;
			StardewModdingAPI.Events.GameEvents.OneSecondTick += (o, e) =>
			{
				secondsPassed = ++secondsPassed % secondsBetweenWarnings;
				if(secondsPassed == 0)
				{
					/* playerIndex 2/3/4 should NOT be active window. player 1 or none is fine */
					if (Context.IsMultiplayer && Game1.game1.IsActive && 
						( playerIndexController.IsPlayerIndexEqual(PlayerIndex.Two)
							|| playerIndexController.IsPlayerIndexEqual(PlayerIndex.Three) 
							|| playerIndexController.IsPlayerIndexEqual(PlayerIndex.Four)) )
						Game1.showRedMessage("Error: This window must not be focused!");
				}
			};

			//Patching Game1.getMousePosition() and Mouse.SetPosition(int x, int y) with Harmony
			//This fixes bugs related to PC only having 1 mouse pointer
			var harmony = HarmonyInstance.Create("me.ilyaki.splitscreen");
			harmony.PatchAll(Assembly.GetExecutingAssembly());
		}
		
		private void OnUnvalidatedUpdateTick (object sender, EventArgs e)
		{
			/*From testing, it seems that between Saving (ie between SaveEvents.BeforeSave and SaveEvents.AfterSave), 
			  GameEvents.UpdateTick is not called (by SMAPI). SpecializedEvents.UnvalidatedUpdateTick is always called, however.
			  ShippingMenu is unresponsive during save period when window is inactive
			  Workaround: Update gamepad/mouse with UnvalidatedUpdateTick*/
			  
			if (!Game1.game1.IsActive && Game1.activeClickableMenu != null && Game1.activeClickableMenu is ShippingMenu shippingMenu)
			{
				UpdateGamePadInput();
				UpdateFakeMouseInput();
			}
		}

		private void OnUpdateTick(object sender, EventArgs e)
		{
			#region Insert raw GamePadState/MouseState to SInputState
			try
			{
				UpdateGamePadInput();

				if (!Game1.game1.IsActive)//Otherwise breaks mouse input for active window
					UpdateFakeMouseInput();
			}
			catch (ArgumentOutOfRangeException exception)
			{
				Monitor.Log("Failed to set input: " + exception.Message, LogLevel.Error);
			}
			#endregion

			#region Manually call UpdateControlInput
			if (
				(!Game1.game1.IsSaving
				&& Game1.gameMode != 11 && (Game1.gameMode == 2 || Game1.gameMode == 3)
				&& Game1.currentLocation != null
				&& Game1.currentMinigame == null
				&& Game1.activeClickableMenu == null
				&& !Game1.globalFade
				&& !Game1.freezeControls
				&& !Game1.game1.IsActive)

			|| (Game1.farmEvent == null
				&& Game1.dialogueUp == true
				&& Game1.globalFade
				&& Game1.gameMode != 11
				&& !Game1.game1.IsSaving
				&& !Game1.game1.IsActive
				&& Game1.currentMinigame == null
				//&&Game1._newDayTask == null (cant access due to protection level, but probably not necessary...?)
				)
			)
			{
				Game1.game1.InvokeMethod("UpdateControlInput", Game1.currentGameTime);
			}
			#endregion

			#region Manually update minigames
			//Kill mouse otherwise it keeps moving when in minigame (which would interfere with the other players)
			var cursorSpeed = Helper.Reflection.GetField<float>(typeof(Game1), "_cursorSpeed");
			var cursorSpeedDirty = Helper.Reflection.GetField<bool>(typeof(Game1), "_cursorSpeedDirty");
			if (Game1.currentMinigame != null)
			{
				cursorSpeed.SetValue(0f);
				cursorSpeedDirty.SetValue(false);
			}
			else if (cursorSpeed.GetValue() == 0)//This happens once after leaving the minigame
			{
				cursorSpeedDirty.SetValue(true);
			}

			if (!Game1.fadeToBlack
				&& Game1.currentMinigame != null
				&& !Game1.HostPaused
				&& (Game1.gameMode == 3 || Game1.gameMode == 2) && (Game1.gameMode != 11)
				&& !Game1.game1.IsSaving
				//&& Game1._newDayTask == null
				&& !Game1.game1.IsActive )
			{
				MinigameUpdater.UpdateMinigameInput(Helper.Reflection, this.playerIndexController);
			}
			#endregion
		}	
		
		private void UpdateGamePadInput()
		{
			//If no playerIndex is set, a blank input state will be passed in
			GamePadState rawGamePadState = playerIndexController.GetRawGamePadState();

			//This only has effect when window is inactive, because otherwise , updateControlInput is called BEFORE this
			sInputState.SetPrivatePropertyValue("RealController", rawGamePadState);
			sInputState.SetPrivatePropertyValue("SuppressedController", rawGamePadState);
		}

		private void UpdateFakeMouseInput()
		{
			//Only call this when inactive
			MouseState artificialMouseState = new MouseState(FakeMouse.X, FakeMouse.Y, 0, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released, ButtonState.Released);

			sInputState.SetPrivatePropertyValue("RealMouse", artificialMouseState);
			sInputState.SetPrivatePropertyValue("MousePosition", FakeMouse.GetPoint());
			sInputState.SetPrivatePropertyValue("SuppressedMouse", artificialMouseState);
		}
	}
}
