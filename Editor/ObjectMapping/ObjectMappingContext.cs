using System;
using System.Linq;
using Anatawa12.AvatarOptimizer.API;
using Anatawa12.AvatarOptimizer.APIInternal;
using nadena.dev.ndmf;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Anatawa12.AvatarOptimizer
{
    internal class ObjectMappingContext : IExtensionContext
    {
        public ObjectMappingBuilder<PropertyInfo>? MappingBuilder { get; private set; }

        public void OnActivate(BuildContext context)
        {
            var avatarTagComponent = context.AvatarRootObject.GetComponentInChildren<AvatarTagComponent>(true);
            if (avatarTagComponent == null)
                MappingBuilder = null;
            else
                MappingBuilder = new ObjectMappingBuilder<PropertyInfo>(context.AvatarRootObject);
        }

        public void OnDeactivate(BuildContext context)
        {
            if (MappingBuilder == null) return;

            var mapping = MappingBuilder.BuildObjectMapping();
            var mappingSource = new MappingSourceImpl(mapping);

            // replace all objects
            foreach (var component in context.GetComponents<Component>())
            {
                using (ErrorReport.WithContextObject(component))
                {
                    if (component is Transform) continue;

                    // apply special mapping
                    if (ComponentInfoRegistry.TryGetInformation(component.GetType(), out var info))
                        info.ApplySpecialMappingInternal(component, mappingSource);

                    var mapAnimatorController = false;

                    switch (component)
                    {
                        case Animator _:
                        case VRC.SDK3.Avatars.Components.VRCAvatarDescriptor _:
#if AAO_VRM0
                        case VRM.VRMBlendShapeProxy _:
#endif
#if AAO_VRM1
                        case UniVRM10.Vrm10Instance _:
#endif
                            mapAnimatorController = true;
                            break;
                    }

                    var serialized = new SerializedObject(component);
                    AnimatorControllerMapper? mapper = null;

                    foreach (var p in serialized.ObjectReferenceProperties())
                    {
                        if (mapping.MapComponentInstance(p.objectReferenceInstanceIDValue, out var mappedComponent))
                            p.objectReferenceValue = mappedComponent;

                        if (mapAnimatorController)
                        {
                            var objectReferenceValue = p.objectReferenceValue;
                            switch (objectReferenceValue)
                            {
                                case RuntimeAnimatorController runtimeController:
                                    mapper ??= new AnimatorControllerMapper(
                                        mapping.CreateAnimationMapper(component.gameObject), context);

                                    // all RuntimeAnimatorControllers in those components should be flattened to
                                    // AnimatorController
                                    mapper.FixAnimatorController(runtimeController);
                                    break;
#if AAO_VRM0
                                case VRM.BlendShapeAvatar avatar:
                                    mapper ??= new AnimatorControllerMapper(
                                        mapping.CreateAnimationMapper(component.gameObject), context);
                                    mapper.FixBlendShapeAvatar(avatar);
                                    break;
#endif
#if AAO_VRM1
                                case UniVRM10.VRM10Object vrm10Object:
                                    mapper ??= new AnimatorControllerMapper(
                                        mapping.CreateAnimationMapper(component.gameObject), context);
                                    mapper.FixVRM10Object(vrm10Object);
                                    break;
#endif
                            }
                        }
                    }

                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
            }
        }
    }

    class MappingSourceImpl : MappingSource
    {
        private readonly ObjectMapping _mapping;

        public MappingSourceImpl(ObjectMapping mapping)
        {
            _mapping = mapping;
        }

        private MappedComponentInfo<T> GetMappedInternal<T>(T component) where T : Object
        {
            var componentInfo = _mapping.GetComponentMapping(component.GetInstanceID());
            if (componentInfo == null) return new OriginalComponentInfo<T>(component);
            return new ComponentInfo<T>(componentInfo);
        }

        public override MappedComponentInfo<T> GetMappedComponent<T>(T component) =>
            GetMappedInternal(component);

        public override MappedComponentInfo<GameObject> GetMappedGameObject(GameObject component) =>
            GetMappedInternal(component);

        private class OriginalComponentInfo<T> : MappedComponentInfo<T> where T : Object
        {
            private readonly T _component;

            public OriginalComponentInfo(T component) => _component = component;

            public override T MappedComponent => _component;
            public override bool TryMapProperty(string property, out API.MappedPropertyInfo found)
            {
                found = new API.MappedPropertyInfo(_component, property);
                return true;
            }
        }

        private class ComponentInfo<T> : MappedComponentInfo<T> where T : Object
        {
            private readonly ComponentInfo _info;

            public ComponentInfo(ComponentInfo info) => _info = info;

            public override T MappedComponent => (T)EditorUtility.InstanceIDToObject(_info.MergedInto);
            public override bool TryMapProperty(string property, out API.MappedPropertyInfo found)
            {
                found = default;

                if (!_info.PropertyMapping.TryGetValue(property, out var mappedProp))
                {
                    found = new API.MappedPropertyInfo(MappedComponent, property);
                    return true;
                }
                if (mappedProp.MappedProperty == default) return false;

                found = new API.MappedPropertyInfo(
                    EditorUtility.InstanceIDToObject(mappedProp.MappedProperty.InstanceId),
                    mappedProp.MappedProperty.Name);
                return true;

            }
        }
    }

    internal class AnimatorControllerMapper
    {
        private readonly AnimationObjectMapper _mapping;
        private readonly BuildContext _context;

        public AnimatorControllerMapper(AnimationObjectMapper mapping, BuildContext context)
        {
            _mapping = mapping;
            _context = context;
        }

        private void ValidateTemporaryAsset(Object asset)
        {
            if (asset == null) return;
            if (!_context.IsTemporaryAsset(asset))
                throw new ArgumentException("asset is not temporary asset", nameof(asset));
        }

        public void FixAnimatorController(RuntimeAnimatorController? controller)
        {
            if (controller == null) return;
            ValidateTemporaryAsset(controller);
            if (controller is not AnimatorController animatorController)
                throw new ArgumentException($"controller {controller.name} is not AnimatorController", nameof(controller));
            FixAnimatorController(animatorController);
        }

        public void FixAnimatorController(AnimatorController? controller)
        {
            if (controller == null) return;
            ValidateTemporaryAsset(controller);
            var layers = controller.layers;
            foreach (var layer in layers)
            {
                FixAvatarMask(layer.avatarMask);
                foreach (var animatorState in ACUtils.AllStates(layer.stateMachine))
                    animatorState.motion = MapMotion(animatorState.motion);
            }
            controller.layers = layers;
            foreach (var stateMachineBehaviour in ACUtils.StateMachineBehaviours(controller))
            {
                FixStateMachineBehaviour(stateMachineBehaviour);
            }
        }

        public Motion? MapMotion(Motion? motion)
        {
            if (motion == null) return null;
            ValidateTemporaryAsset(motion);
            switch (motion)
            {
                case AnimationClip clip:
                    return MapClip(clip);
                case BlendTree blendTree:
                    var children = blendTree.children;
                    foreach (ref var childMotion in children.AsSpan())
                        childMotion.motion = MapMotion(childMotion.motion);
                    blendTree.children = children;
                    return blendTree;
                default:
                    throw new NotSupportedException($"Unsupported motion type: {motion.GetType()}");
            }
        }

        public AnimationClip? MapClip(AnimationClip? clip)
        {
            if (clip == null) return null;
#if AAO_VRCSDK3_AVATARS
            // TODO: when BuildContext have property to check if it is for VRCSDK3, additionally use it.
            if (clip.IsProxy()) return clip;
#endif
            var newClip = new AnimationClip();
            ObjectRegistry.RegisterReplacedObject(clip, newClip);
            newClip.name = "rebased " + clip.name;

            // copy m_UseHighQualityCurve with SerializedObject since m_UseHighQualityCurve doesn't have public API
            using (var serializedClip = new SerializedObject(clip))
            using (var serializedNewClip = new SerializedObject(newClip))
            {
                serializedNewClip.FindProperty("m_UseHighQualityCurve")
                    .boolValue = serializedClip.FindProperty("m_UseHighQualityCurve").boolValue;
                serializedNewClip.ApplyModifiedPropertiesWithoutUndo();
            }

            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var newBindings = _mapping.MapBinding(binding.path, binding.type, binding.propertyName);
                if (newBindings == null)
                {
                    newClip.SetCurve(binding.path, binding.type, binding.propertyName,
                        AnimationUtility.GetEditorCurve(clip, binding));
                }
                else
                {
                    foreach (var newBinding in newBindings)
                    {
                        newClip.SetCurve(newBinding.path, newBinding.type, newBinding.propertyName,
                            AnimationUtility.GetEditorCurve(clip, binding));
                    }
                }
            }

            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(clip))
            {
                var newBindings = _mapping.MapBinding(binding.path, binding.type, binding.propertyName);
                if (newBindings == null)
                {
                    AnimationUtility.SetObjectReferenceCurve(newClip, binding,
                        AnimationUtility.GetObjectReferenceCurve(clip, binding));
                }
                else
                {
                    foreach (var tuple in newBindings)
                    {
                        var newBinding = binding;
                        newBinding.path = tuple.path;
                        newBinding.type = tuple.type;
                        newBinding.propertyName = tuple.propertyName;
                        AnimationUtility.SetObjectReferenceCurve(newClip, newBinding,
                            AnimationUtility.GetObjectReferenceCurve(clip, binding));
                    }
                }
            }

            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (newClip.length != clip.length)
            {
                // if newClip has less properties than original clip (especially for no properties), 
                // length of newClip can be changed which is bad.
                newClip.SetCurve(
                    "$AvatarOptimizerClipLengthDummy$", typeof(GameObject), Props.IsActive,
                    AnimationCurve.Constant(clip.length, clip.length, 1f));
            }

            newClip.wrapMode = clip.wrapMode;
            newClip.legacy = clip.legacy;
            newClip.frameRate = clip.frameRate;
            newClip.localBounds = clip.localBounds;
            var settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.additiveReferencePoseClip = MapClip(settings.additiveReferencePoseClip);
            AnimationUtility.SetAnimationClipSettings(newClip, settings);

            return newClip;
        }

        public void FixStateMachineBehaviour(StateMachineBehaviour? stateMachineBehaviour)
        {
            if (stateMachineBehaviour == null) return;
            ValidateTemporaryAsset(stateMachineBehaviour);
#if AAO_VRCSDK3_AVATARS
            if (stateMachineBehaviour is VRC.SDKBase.VRC_AnimatorPlayAudio playAudio)
            {
                if (playAudio.SourcePath != null)
                    playAudio.SourcePath = _mapping.MapPath(playAudio.SourcePath, typeof(AudioSource));
            }
#endif
        }

        public void FixAvatarMask(AvatarMask? mask)
        {
            if (mask == null) return;
            ValidateTemporaryAsset(mask);
            var dstI = 0;
            for (var srcI = 0; srcI < mask.transformCount; srcI++)
            {
                var path = mask.GetTransformPath(srcI);
                var newPath = _mapping.MapPath(path, typeof(Transform));
                if (newPath != null)
                {
                    mask.SetTransformPath(dstI, newPath);
                    mask.SetTransformActive(dstI, mask.GetTransformActive(srcI));
                    dstI++;
                }
            }
            mask.transformCount = dstI;
        }

#if AAO_VRM0
        public void FixBlendShapeAvatar(VRM.BlendShapeAvatar? blendShapeAvatar)
        {
            if (blendShapeAvatar == null) return;
            ValidateTemporaryAsset(blendShapeAvatar);
            foreach (var clip in blendShapeAvatar.Clips)
                FixVRMBlendShapeClip(clip);
        }

        public void FixVRMBlendShapeClip(VRM.BlendShapeClip? blendShapeClip)
        {
            if (blendShapeClip == null) return;
            ValidateTemporaryAsset(blendShapeClip);
            blendShapeClip.Values = blendShapeClip.Values.SelectMany(binding =>
            {
                var mappedBindings = _mapping.MapBinding(binding.RelativePath, typeof(SkinnedMeshRenderer), VProp.BlendShapeIndex(binding.Index));
                if (mappedBindings == null)
                {
                    return new[] { binding };
                }
                return mappedBindings
                    .Select(mapped => new VRM.BlendShapeBinding
                    {
                        RelativePath = _mapping.MapPath(mapped.path, typeof(SkinnedMeshRenderer)),
                        Index = VProp.ParseBlendShapeIndex(mapped.propertyName),
                        Weight = binding.Weight
                    });
            }).ToArray(); 
            // Currently, MaterialValueBindings are guaranteed to not change (MaterialName, in particular)
            // unless MergeToonLitMaterial is used, which breaks material animations anyway.
            // Map MaterialValues here once we start tracking material changes...
        }
#endif

#if AAO_VRM1
        public void FixVRM10Object(UniVRM10.VRM10Object? vrm10Object)
        {
            if (vrm10Object == null) return;
            ValidateTemporaryAsset(vrm10Object);
            foreach (var customClip in vrm10Object.Expression.CustomClips)
                FixVRM10Expression(customClip);

            vrm10Object.FirstPerson.Renderers = vrm10Object.FirstPerson.Renderers
                .Select(r => new UniVRM10.RendererFirstPersonFlags
                {
                    Renderer = _mapping.MapPath(r.Renderer, typeof(Renderer)),
                    FirstPersonFlag = r.FirstPersonFlag
                })
                .Where(r => r.Renderer != null)
                .GroupBy(r => r.Renderer, r => r.FirstPersonFlag)
                .Select(grouping =>
                {
                    UniGLTF.Extensions.VRMC_vrm.FirstPersonType mergedFirstPersonFlag;
                    var firstPersonFlags = grouping.Distinct().ToArray();
                    if (firstPersonFlags.Length == 1)
                    {
                        mergedFirstPersonFlag = firstPersonFlags[0];
                    }
                    else
                    {
                        mergedFirstPersonFlag = firstPersonFlags.Contains(UniGLTF.Extensions.VRMC_vrm.FirstPersonType.both) ? UniGLTF.Extensions.VRMC_vrm.FirstPersonType.both : UniGLTF.Extensions.VRMC_vrm.FirstPersonType.auto;
                        BuildLog.LogWarning("MergeSkinnedMesh:warning:VRM:FirstPersonFlagsMismatch", mergedFirstPersonFlag.ToString());
                    }

                    return new UniVRM10.RendererFirstPersonFlags
                    {
                        Renderer = grouping.Key,
                        FirstPersonFlag = mergedFirstPersonFlag
                    };
                }).ToList();
        }

        public void FixVRM10Expression(UniVRM10.VRM10Expression? vrm10Expression)
        {
            if (vrm10Expression == null) return;
            ValidateTemporaryAsset(vrm10Expression);
            vrm10Expression.Prefab = null; // This likely to point prefab before mapping, which is invalid by now
            vrm10Expression.MorphTargetBindings = vrm10Expression.MorphTargetBindings.SelectMany(binding =>
            {
                var mappedBindings = _mapping.MapBinding(binding.RelativePath, typeof(SkinnedMeshRenderer), VProp.BlendShapeIndex(binding.Index));
                if (mappedBindings == null)
                {
                    return new[] { binding };
                }
                return mappedBindings
                    .Select(mapped => new UniVRM10.MorphTargetBinding
                    {
                        RelativePath = _mapping.MapPath(mapped.path, typeof(SkinnedMeshRenderer)),
                        Index = VProp.ParseBlendShapeIndex(mapped.propertyName),
                        Weight = binding.Weight
                    });
            }).ToArray(); 
            // Currently, MaterialColorBindings and MaterialUVBindings are guaranteed to not change (MaterialName, in particular)
            // unless MergeToonLitMaterial is used, which breaks material animations anyway.
            // Map MaterialColorBindings / MaterialUVBindings here once we start tracking material changes...
        }
#endif
    }
}
