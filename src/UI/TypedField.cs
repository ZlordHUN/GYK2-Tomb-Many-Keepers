using LazyBearTechnology;
using TMPro;
using UnityEngine;

namespace GYK2.TombManyKeepers.UI;

// While the player types in one of the mod's text fields, the game's own keys wait, as its own text fields make
// them, so typing a letter presses nothing; Escape leaves the field.
internal sealed class TypedField : MonoBehaviour
{
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
}
