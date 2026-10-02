using System.Collections.Generic;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;

namespace GYK2.TombManyKeepers.UI;

// While the player types in one of the mod's text fields, the game's own keys wait, as its own text fields make
// them, so typing a letter presses nothing; Escape leaves the field. The keys already down let go as typing starts,
// so a keeper walking stops instead of walking on until the typing ends.
internal sealed class TypedField : MonoBehaviour
{
    private static readonly AccessTools.FieldRef<LazyInput> Keys =
        AccessTools.StaticFieldRefAccess<LazyInput>(AccessTools.Field(typeof(LazyInput), "instance"));
    private static readonly AccessTools.FieldRef<LazyInput, List<GameKey>> Held =
        AccessTools.FieldRefAccess<LazyInput, List<GameKey>>("holdedKeys");
    private static readonly AccessTools.FieldRef<LazyInput, List<GameKey>> Pressed =
        AccessTools.FieldRefAccess<LazyInput, List<GameKey>>("pressedKeys");
    private static readonly AccessTools.FieldRef<LazyInput, Vector2> Direction =
        AccessTools.FieldRefAccess<LazyInput, Vector2>("direction");
    private static readonly AccessTools.FieldRef<LazyInput, Vector2> Direction2 =
        AccessTools.FieldRefAccess<LazyInput, Vector2>("direction2");
    private TMP_InputField field;
    private bool suspended;
    private bool wasActive;

    internal static void Guard(TMP_InputField field) => field.gameObject.AddComponent<TypedField>().field = field;

    private void LateUpdate()
    {
        bool typing = field.isFocused;
        if (typing && !suspended)
        {
            wasActive = LazyInput.IsInputActive();
            LazyInput.SetInputActivity(isInputActive: false);
            LetGo();
            suspended = true;
        }
        else if (!typing && suspended)
        {
            Restore();
        }
        if (typing && Input.GetKeyDown(KeyCode.Escape))
            field.DeactivateInputField();
    }

    private void OnDisable()
    {
        if (suspended)
            Restore();
    }

    private void Restore()
    {
        suspended = false;
        LazyInput.SetInputActivity(wasActive);
    }

    // The game reads its keys again only once typing ends; meanwhile nothing counts as held.
    private static void LetGo()
    {
        var input = Keys();
        if (input == null)
            return;
        Held(input).Clear();
        Pressed(input).Clear();
        Direction(input) = Vector2.zero;
        Direction2(input) = Vector2.zero;
    }
}
