using Landfall.Modding;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System.Collections;
using System.Reflection;
using MonoMod.RuntimeDetour;

namespace BetterSettings;

[LandfallPlugin]
public class BetterSettings
{
    // Hooks
    private static Hook? OnEnableHook;
    private static Hook? SelectTabHook; // Hook for the tab selection method

    // Constants
    private const float HORIZONTAL_PADDING = 25f;
    private const float DEFAULT_TAB_HEIGHT = 30f;

    // State
    private static bool scrollSetupDone = false;
    private static Coroutine? resizeCoroutine = null;

    static BetterSettings()
    {
        scrollSetupDone = false;
        try
        {
            // Hook SettingsUIPage.OnEnable (as before)
            MethodInfo? onEnableMethodInfo = typeof(SettingsUIPage).GetMethod(
                "OnEnable", BindingFlags.Instance | BindingFlags.NonPublic);
            if (onEnableMethodInfo == null) { /* Error Log */ return; }
            OnEnableHook = new Hook(onEnableMethodInfo,
                new Action<Action<SettingsUIPage>, SettingsUIPage>(SettingsUIPage_OnEnable));
            Debug.Log("[TabResizeMod] Hooked SettingsUIPage.OnEnable.");

            // --- Hook SettingsTABS.Select ---
            // *** VERIFY METHOD NAME AND SIGNATURE ***
            // Assuming public void Select(SettingTABSButton buttonToSelect)
            // If private, use BindingFlags.NonPublic as well.
            MethodInfo? selectMethodInfo = typeof(SettingsTABS).GetMethod(
                "Select", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, // Try both public/non-public
                null, // Binder
                new Type[] { typeof(SettingTABSButton) }, // Argument types
                null // Modifiers
            );

            // Fallback if "Select" with specific arg not found, maybe parameterless? Or different name?
            if (selectMethodInfo == null)
            {
                 // Example: Try finding a method that *sets* selectedButton?
                 // selectMethodInfo = typeof(SettingsTABS).GetProperty("selectedButton")?.GetSetMethod(true); // true for non-public setter
                 Debug.LogWarning("[TabResizeMod] Could not find SettingsTABS.Select(SettingTABSButton). Trying SelectNext/Previous...");
                 // If Select isn't the one, hook SelectNext/SelectPrevious individually
                 HookIndividualNavMethods(); // Call helper to hook SelectNext/Previous
            }

            if (selectMethodInfo != null)
            {
                 // Hook signature matches our SettingsTABS_Select method
                 SelectTabHook = new Hook(selectMethodInfo,
                     new Action<Action<SettingsTABS, SettingTABSButton>, SettingsTABS, SettingTABSButton>(
                         SettingsTABS_Select
                     ));
                 Debug.Log($"[TabResizeMod] Successfully hooked SettingsTABS.{selectMethodInfo.Name}.");
            }
            else if (!didHookIndividualNavMethods) // Only log error if fallback also failed
            {
                 Debug.LogError("[TabResizeMod] Failed to find SettingsTABS.Select or SelectNext/Previous method! Controller scrolling won't work.");
            }

        }
        catch (Exception e)
        {
            Debug.LogError($"[TabResizeMod] Exception during hooking: {e}");
        }
    }

    private static bool didHookIndividualNavMethods = false;
    private static Hook? SelectNextHook;
    private static Hook? SelectPreviousHook;

    // Helper to hook SelectNext/SelectPrevious if Select(button) isn't found
    private static void HookIndividualNavMethods()
    {
        try
        {
            MethodInfo? nextMethod = typeof(SettingsTABS).GetMethod("SelectNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            MethodInfo? prevMethod = typeof(SettingsTABS).GetMethod("SelectPrevious", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (nextMethod != null)
            {
                SelectNextHook = new Hook(nextMethod, new Action<Action<SettingsTABS>, SettingsTABS>(SettingsTABS_SelectNextPrevious));
                Debug.Log("[TabResizeMod] Hooked SettingsTABS.SelectNext.");
                didHookIndividualNavMethods = true;
            }
             if (prevMethod != null)
            {
                SelectPreviousHook = new Hook(prevMethod, new Action<Action<SettingsTABS>, SettingsTABS>(SettingsTABS_SelectNextPrevious));
                Debug.Log("[TabResizeMod] Hooked SettingsTABS.SelectPrevious.");
                didHookIndividualNavMethods = true;
            }
        }
        catch (Exception e)
        {
             Debug.LogError($"[TabResizeMod] Exception hooking SelectNext/Previous: {e}");
        }
    }


    // --- Hook for SettingsTABS.Select(SettingTABSButton) ---
    private static void SettingsTABS_Select(
        Action<SettingsTABS, SettingTABSButton> orig, // Original Select method
        SettingsTABS self,                           // Instance of SettingsTABS
        SettingTABSButton buttonToSelect             // The button being selected
    )
    {
        orig(self, buttonToSelect); // Call the original selection logic first

        if (buttonToSelect != null)
        {
            Debug.Log($"[TabResizeMod] SettingsTABS_Select called for: {buttonToSelect.name}. Scrolling if needed.");
            ScrollToMakeTabVisible(self, buttonToSelect);
        }
    }

    // --- Hook for SettingsTABS.SelectNext() or SelectPrevious() ---
    // This is used if we couldn't hook a Select method that takes the button directly.
    // We call the original, then find the *currently selected* button afterwards.
    private static void SettingsTABS_SelectNextPrevious(
        Action<SettingsTABS> orig, // Original SelectNext or SelectPrevious
        SettingsTABS self          // Instance of SettingsTABS
    )
    {
         // Store button *before* calling orig, in case orig changes it immediately
         SettingTABSButton previouslySelected = self.selectedButton;

         orig(self); // Call the original SelectNext or SelectPrevious

         // Now check the *newly* selected button
         SettingTABSButton newlySelected = self.selectedButton;

         // Only scroll if the selection actually changed
         if (newlySelected != null && newlySelected != previouslySelected)
         {
             Debug.Log($"[TabResizeMod] SettingsTABS_SelectNextPrevious executed. New selection: {newlySelected.name}. Scrolling if needed.");
             ScrollToMakeTabVisible(self, newlySelected);
         } else if (newlySelected == previouslySelected) {
             Debug.Log($"[TabResizeMod] SettingsTABS_SelectNextPrevious executed, but selection didn't change.");
         }
    }


    // --- Helper method to perform the scrolling calculation ---
    private static void ScrollToMakeTabVisible(SettingsTABS tabsInstance, SettingTABSButton selectedTab)
    {
        // Get necessary components (traverse hierarchy from SettingsTABS instance)
        RectTransform contentRect = tabsInstance.GetComponent<RectTransform>();
        if (contentRect == null) return;

        Transform viewportTransform = contentRect.parent;
        if (viewportTransform == null) return;
        RectTransform viewportRect = viewportTransform.GetComponent<RectTransform>();
        if (viewportRect == null) return;

        Transform scrollRectTransform = viewportTransform.parent;
        if (scrollRectTransform == null) return;
        ScrollRect scrollRect = scrollRectTransform.GetComponent<ScrollRect>();
        if (scrollRect == null) return;

        RectTransform selectedTabRect = selectedTab.GetComponent<RectTransform>();
        if (selectedTabRect == null) return;

        // Calculate widths
        float viewportWidth = viewportRect.rect.width;
        float contentWidth = contentRect.rect.width;
        float tabWidth = selectedTabRect.rect.width;

        // Calculate tab position relative to the content's left edge
        // anchoredPosition.x assumes pivot is somewhere along the horizontal axis.
        // We need the position of the tab's left edge within the content's coordinate system.
        // A more robust way considers pivot: tabLeft = tabRect.anchoredPosition.x - tabRect.pivot.x * tabWidth;
        // Even better: Convert world corners to local space of content.
        Vector3 tabLeftWorld = selectedTabRect.TransformPoint(selectedTabRect.rect.min);
        Vector3 tabRightWorld = selectedTabRect.TransformPoint(selectedTabRect.rect.max);
        float tabLeftLocal = contentRect.InverseTransformPoint(tabLeftWorld).x;
        float tabRightLocal = contentRect.InverseTransformPoint(tabRightWorld).x;
        // Recalculate tabWidth based on local coordinates for consistency
        tabWidth = tabRightLocal - tabLeftLocal;


        // Calculate currently visible bounds in content's local space
        float visibleLeftLocal = -contentRect.anchoredPosition.x; // Assumes content pivot is 0,0 or 0,y
        // More general calculation using normalized position:
        // visibleLeftLocal = scrollRect.horizontalNormalizedPosition * (contentWidth - viewportWidth); // Only if contentWidth > viewportWidth
        visibleLeftLocal = Mathf.Lerp(0, contentWidth - viewportWidth, scrollRect.horizontalNormalizedPosition); // Handles contentWidth <= viewportWidth case better
        float visibleRightLocal = visibleLeftLocal + viewportWidth;


        // Check if already visible
        bool isVisible = tabLeftLocal >= visibleLeftLocal && tabRightLocal <= visibleRightLocal;

        Debug.Log($"[TabResizeMod] Check Visibility: Tab='{selectedTab.name}' ({tabLeftLocal:F1} to {tabRightLocal:F1}, W:{tabWidth:F1}), Viewport=({visibleLeftLocal:F1} to {visibleRightLocal:F1}, W:{viewportWidth:F1}), ContentW:{contentWidth:F1}, NormPos:{scrollRect.horizontalNormalizedPosition:F2}");


        if (isVisible)
        {
            Debug.Log("[TabResizeMod] Tab already visible, no scroll needed.");
            return;
        }

        // Calculate target normalized position to center the tab
        float targetTabCenterLocal = tabLeftLocal + tabWidth * 0.5f;
        float targetScrollPosition = 0f; // Default to left edge

        // Avoid division by zero or negative if content fits or is smaller than viewport
        if (contentWidth > viewportWidth)
        {
            // Formula: TargetScrollPos = (TargetCenterInContent - HalfViewportWidth) / (ContentWidth - ViewportWidth)
            targetScrollPosition = (targetTabCenterLocal - viewportWidth * 0.5f) / (contentWidth - viewportWidth);
        }

        // Clamp position between 0 and 1
        targetScrollPosition = Mathf.Clamp01(targetScrollPosition);

        Debug.Log($"[TabResizeMod] Scrolling needed. Target normalized position: {targetScrollPosition:F3}");

        // --- Apply Scroll Position ---
        // Option 1: Direct Set (Instant)
        scrollRect.horizontalNormalizedPosition = targetScrollPosition;

        // Option 2: Smooth Scroll (Requires starting a coroutine)
        // StopCoroutine(ScrollCoroutine); // Stop previous smooth scroll if any
        // ScrollCoroutine = StartCoroutine(SmoothScroll(scrollRect, targetScrollPosition, 0.15f));
    }

    // --- Coroutine for Smooth Scrolling (Optional) ---
    /*
    private static Coroutine ScrollCoroutine;
    private static IEnumerator SmoothScroll(ScrollRect scrollRect, float targetNormalizedPos, float duration)
    {
        float startingPos = scrollRect.horizontalNormalizedPosition;
        float elapsedTime = 0f;

        while (elapsedTime < duration)
        {
            elapsedTime += Time.unscaledDeltaTime; // Use unscaled time for UI
            float t = Mathf.Clamp01(elapsedTime / duration);
            // Optional: Add easing (e.g., SmoothStep)
            // t = t * t * (3f - 2f * t);
            scrollRect.horizontalNormalizedPosition = Mathf.Lerp(startingPos, targetNormalizedPos, t);
            yield return null; // Wait for next frame
        }
        scrollRect.horizontalNormalizedPosition = targetNormalizedPos; // Ensure final position is exact
        ScrollCoroutine = null;
    }
    */


    // --- OnEnable and SetupScrollView (Unchanged from previous version) ---
    private static void SettingsUIPage_OnEnable(Action<SettingsUIPage> orig, SettingsUIPage self)
    {
        orig(self);
        Debug.Log("[TabResizeMod] SettingsUIPage_OnEnable hook executing...");
        if (!scrollSetupDone)
        {
            if (self.m_tabs == null || self.m_tabs.transform == null) { /* Error Log */ return; }
            SetupScrollView(self.m_tabs, self);
            scrollSetupDone = true;
        }
        if (self.m_tabs != null && self.m_tabs.transform != null)
        {
             HorizontalLayoutGroup? layoutGroup = self.m_tabs.GetComponent<HorizontalLayoutGroup>();
             if (layoutGroup != null)
             {
                if (resizeCoroutine != null) { self.StopCoroutine(resizeCoroutine); }
                resizeCoroutine = self.StartCoroutine(ResizeTabsEndOfFrame(self.m_tabs, layoutGroup));
             } else { /* Error Log */ }
        } else { /* Error Log */ }
    }

    private static void SetupScrollView(SettingsTABS tabsComponent, SettingsUIPage settingsPageInstance)
    {
        // ... (SetupScrollView code remains exactly the same as the previous working version) ...
        Debug.Log("[TabResizeMod] Performing Scroll View Setup...");

        GameObject contentGO = tabsComponent.gameObject;
        RectTransform contentRect = contentGO.GetComponent<RectTransform>();
        if (contentRect == null) { Debug.LogError("[TabResizeMod] Content GameObject missing RectTransform!"); return; }

        Transform originalParentTransform = contentRect.parent;
        if (originalParentTransform == null) { Debug.LogError("[TabResizeMod] Content's original parent is null."); return; }
        RectTransform originalParentRect = originalParentTransform.GetComponent<RectTransform>();
         if (originalParentRect == null) { Debug.LogWarning("[TabResizeMod] Original Parent GameObject missing RectTransform!");}


        GameObject viewportGO = new GameObject("TabScrollView_Viewport");
        RectTransform viewportRect = viewportGO.AddComponent<RectTransform>();
        viewportGO.transform.SetParent(originalParentTransform, false);

        viewportRect.anchorMin = contentRect.anchorMin;
        viewportRect.anchorMax = contentRect.anchorMax;
        viewportRect.pivot = contentRect.pivot;
        viewportRect.anchoredPosition = contentRect.anchoredPosition;
        viewportRect.sizeDelta = contentRect.sizeDelta;
        viewportRect.localScale = contentRect.localScale;
        Debug.Log($"[TabResizeMod] Copied RectTransform settings from {contentGO.name} to Viewport.");


        Image viewportImage = viewportGO.AddComponent<Image>();
        viewportImage.color = new Color(0, 0, 0, 0.01f);
        viewportImage.raycastTarget = false;
        Mask viewportMask = viewportGO.AddComponent<Mask>();
        viewportMask.showMaskGraphic = false;
        Debug.Log("[TabResizeMod] Added Image and Mask to Viewport.");

        contentRect.SetParent(viewportRect, false);

        contentRect.anchorMin = new Vector2(0, 0);
        contentRect.anchorMax = new Vector2(0, 1);
        contentRect.pivot = new Vector2(0, 0.5f);
        contentRect.sizeDelta = new Vector2(0, 0);
        contentRect.anchoredPosition = Vector2.zero;
        contentRect.localScale = Vector3.one;
        Debug.Log("[TabResizeMod] Reparented Content and configured its RectTransform for scrolling.");

        HorizontalLayoutGroup hlg = contentGO.GetComponent<HorizontalLayoutGroup>();
        if (hlg != null)
        {
            hlg.childControlWidth = false;
            hlg.childControlHeight = true;
            hlg.childForceExpandWidth = false;
            hlg.childForceExpandHeight = true;
            hlg.childAlignment = TextAnchor.MiddleLeft;
            Debug.Log("[TabResizeMod] Ensured HLG settings on Content.");
        } else { Debug.LogError("[TabResizeMod] HorizontalLayoutGroup missing on Content object!"); }

        ContentSizeFitter csf = contentGO.GetComponent<ContentSizeFitter>();
        if (csf == null) { csf = contentGO.AddComponent<ContentSizeFitter>(); Debug.Log("[TabResizeMod] Added ContentSizeFitter to Content."); }
        csf.horizontalFit = ContentSizeFitter.FitMode.PreferredSize;
        csf.verticalFit = ContentSizeFitter.FitMode.Unconstrained;

        ScrollRect scrollRect = originalParentTransform.gameObject.GetComponent<ScrollRect>();
        if (scrollRect == null) { scrollRect = originalParentTransform.gameObject.AddComponent<ScrollRect>(); Debug.Log($"[TabResizeMod] Added ScrollRect to Original Parent GameObject: {originalParentTransform.name}."); }

        scrollRect.content = contentRect;
        scrollRect.viewport = viewportRect;
        scrollRect.horizontal = true;
        scrollRect.vertical = false;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        scrollRect.inertia = true;
        scrollRect.decelerationRate = 0.135f;
        scrollRect.scrollSensitivity = 15f;

        Debug.Log("[TabResizeMod] Configured ScrollRect component.");
        Debug.Log("[TabResizeMod] Scroll View Setup Complete.");
    }

    // --- Resize Coroutine (Unchanged from previous version) ---
    private static IEnumerator ResizeTabsEndOfFrame(SettingsTABS tabsComponent, HorizontalLayoutGroup layoutGroup)
    {
        // ... (ResizeTabsEndOfFrame code remains exactly the same as the previous working version) ...
        yield return new WaitForEndOfFrame();
        yield return null;

        Debug.Log("[TabResizeMod] Coroutine: Resizing individual tabs at EndOfFrame...");

        if (tabsComponent == null || tabsComponent.buttons == null) { Debug.LogError("[TabResizeMod] Coroutine: Tabs component or buttons are null."); yield break; }

        bool needsRebuild = false;

        foreach (SettingTABSButton tabButton in tabsComponent.buttons)
        {
            if (tabButton == null) continue;

            RectTransform tabRect = tabButton.GetComponent<RectTransform>();
            TextMeshProUGUI tmpText = tabButton.text;

            if (tabRect == null || tmpText == null) { Debug.LogWarning($"[TabResizeMod] Coroutine: Missing components on tab: {tabButton.name}"); continue; }

            tmpText.ForceMeshUpdate();
            float preferredWidth = tmpText.GetPreferredValues().x;
            if (preferredWidth <= 1f) preferredWidth = 50f;
            float targetWidth = preferredWidth + HORIZONTAL_PADDING;

            float currentHeight = tabRect.rect.height;
            float targetHeight = (currentHeight > 1f) ? currentHeight : DEFAULT_TAB_HEIGHT;

            LayoutElement layoutElement = tabButton.GetComponent<LayoutElement>();
            if (layoutElement != null && layoutElement.enabled) { layoutElement.enabled = false; needsRebuild = true; }
            ContentSizeFitter fitter = tabButton.GetComponent<ContentSizeFitter>();
            if (fitter != null && fitter.enabled) { fitter.enabled = false; needsRebuild = true; }

            Vector2 newSize = new Vector2(targetWidth, targetHeight);
            if (tabRect.sizeDelta != newSize) { tabRect.sizeDelta = newSize; needsRebuild = true; }
        }

        if (needsRebuild && layoutGroup != null)
        {
            Debug.Log("[TabResizeMod] Coroutine: Forcing layout rebuild on Content HLG.");
            RectTransform layoutGroupRect = layoutGroup.GetComponent<RectTransform>();
            if(layoutGroupRect != null) { LayoutRebuilder.ForceRebuildLayoutImmediate(layoutGroupRect); }
            else { Debug.LogError("[TabResizeMod] Coroutine: Could not get RectTransform for HLG rebuild!"); }
        }

        ScrollRect parentScrollRect = layoutGroup?.GetComponentInParent<ScrollRect>();
        if(parentScrollRect != null && parentScrollRect.content != null)
        {
            LayoutRebuilder.MarkLayoutForRebuild(parentScrollRect.content);
             Debug.Log("[TabResizeMod] Coroutine: Marked ScrollRect content for rebuild.");
        } else { Debug.LogWarning("[TabResizeMod] Coroutine: Could not find parent ScrollRect or its content to mark for rebuild."); }

        Debug.Log("[TabResizeMod] Coroutine: Individual tab resizing finished.");
        resizeCoroutine = null;
    }

    // Optional: Dispose hooks
    // public void OnDestroy()
    // {
    //     OnEnableHook?.Dispose();
    //     SelectTabHook?.Dispose(); // Dispose new hook
    //     SelectNextHook?.Dispose();
    //     SelectPreviousHook?.Dispose();
    //     scrollSetupDone = false;
    // }
}
