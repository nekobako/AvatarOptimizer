using System;
using System.Collections.Generic;
using System.Linq;
using Anatawa12.AvatarOptimizer.Processors.TraceAndOptimizes;
using JetBrains.Annotations;
using UnityEditor;
using UnityEngine;

namespace Anatawa12.AvatarOptimizer
{
    internal class AnimationObjectMapper
    {
        readonly GameObject _rootGameObject;
        readonly BeforeGameObjectTree _beforeGameObjectTree;
        readonly ObjectMapping _objectMapping;

        private readonly Dictionary<string, MappedGameObjectInfo> _pathsCache =
            new Dictionary<string, MappedGameObjectInfo>();

        public AnimationObjectMapper(GameObject rootGameObject, BeforeGameObjectTree beforeGameObjectTree,
            ObjectMapping objectMapping)
        {
            _rootGameObject = rootGameObject;
            _beforeGameObjectTree = beforeGameObjectTree;
            _objectMapping = objectMapping;
        }

        // null means nothing to map
        [CanBeNull]
        private MappedGameObjectInfo GetGameObjectInfo(string path)
        {
            if (_pathsCache.TryGetValue(path, out var info)) return info;

            var tree = _beforeGameObjectTree;

            if (path != "")
            {
                foreach (var pathSegment in path.Split('/'))
                {
                    tree = tree.Children.FirstOrDefault(x => x.Name == pathSegment);
                    if (tree == null) break;
                }
            }

            if (tree == null)
            {
                info = null;
            }
            else
            {
                var foundGameObject = EditorUtility.InstanceIDToObject(tree.InstanceId) as GameObject;
                var newPath = foundGameObject
                    ? Utils.RelativePath(_rootGameObject.transform, foundGameObject.transform)
                    : null;

                info = new MappedGameObjectInfo(_objectMapping, newPath, tree);
            }

            _pathsCache.Add(path, info);
            return info;
        }

        class MappedGameObjectInfo
        {
            private ObjectMapping _objectMapping;

            readonly BeforeGameObjectTree _tree;

            // null means removed gameObject
            [CanBeNull] public readonly string NewPath;

            public MappedGameObjectInfo(ObjectMapping objectMapping, string newPath,
                BeforeGameObjectTree tree)
            {
                _objectMapping = objectMapping;
                NewPath = newPath;
                _tree = tree;
            }

            public (int instanceId, ComponentInfo) GetComponentByType(Type type)
            {
                if (!_tree.ComponentInstanceIdByType.TryGetValue(type, out var instanceId))
                    return (instanceId, null); // Nothing to map
                return (instanceId, _objectMapping.GetComponentMapping(instanceId));
            }
        }

        [CanBeNull]
        public string MapPath(string srcPath, Type type)
        {
            var gameObjectInfo = GetGameObjectInfo(srcPath);
            if (gameObjectInfo == null) return srcPath;
            var (instanceId, componentInfo) = gameObjectInfo.GetComponentByType(type);

            if (componentInfo != null)
            {
                var component = EditorUtility.InstanceIDToObject(componentInfo.MergedInto) as Component;
                // there's mapping about component.
                // this means the component is merged or some prop has mapping
                if (!component) return null; // this means removed.

                var newPath = Utils.RelativePath(_rootGameObject.transform, component.transform);
                if (newPath == null) return null; // this means moved to out of the animator scope

                return newPath;
            }
            else
            {
                // The component is not merged & no prop mapping so process GameObject mapping

                if (type != typeof(GameObject))
                {
                    var component = EditorUtility.InstanceIDToObject(instanceId) as Component;
                    if (!component) return null; // this means removed
                }

                if (gameObjectInfo.NewPath == null) return null;
                return gameObjectInfo.NewPath;
            }
        }

        [CanBeNull]
        public EditorCurveBinding[] MapBinding(EditorCurveBinding binding)
        {
            var gameObjectInfo = GetGameObjectInfo(binding.path);
            if (gameObjectInfo == null)
                return null;
            var (instanceId, componentInfo) = gameObjectInfo.GetComponentByType(binding.type);

            if (componentInfo != null)
            {
                if (componentInfo.PropertyMapping.TryGetValue(binding.propertyName, out var newProp))
                {
                    // there are mapping for component
                    var curveBindings = new EditorCurveBinding[newProp.AllCopiedTo.Length];
                    for (var i = 0; i < newProp.AllCopiedTo.Length; i++)
                    {
                        var descriptor = newProp.AllCopiedTo[i];
                        var component = EditorUtility.InstanceIDToObject(descriptor.InstanceId) as Component;
                        // this means removed.
                        if (component == null) continue;

                        var newPath = Utils.RelativePath(_rootGameObject.transform, component.transform);

                        // this means moved to out of the animator scope
                        // TODO: add warning
                        if (newPath == null) return Array.Empty<EditorCurveBinding>();

                        binding.path = newPath;
                        binding.type = descriptor.Type;
                        binding.propertyName = descriptor.Name;
                        curveBindings[i] = binding; // copy
                    }

                    return curveBindings;
                }
                else
                {
                    var component = EditorUtility.InstanceIDToObject(componentInfo.MergedInto) as Component;
                    // there's mapping about component.
                    // this means the component is merged or some prop has mapping
                    if (!component) return Array.Empty<EditorCurveBinding>(); // this means removed.

                    var newPath = Utils.RelativePath(_rootGameObject.transform, component.transform);
                    if (newPath == null) return Array.Empty<EditorCurveBinding>(); // this means moved to out of the animator scope
                    if (binding.path == newPath) return null;
                    binding.path = newPath;
                    return new []{ binding };
                }
            }
            else
            {
                // The component is not merged & no prop mapping so process GameObject mapping

                if (binding.type != typeof(GameObject))
                {
                    var component = EditorUtility.InstanceIDToObject(instanceId) as Component;
                    if (!component) return Array.Empty<EditorCurveBinding>(); // this means removed
                }

                if (gameObjectInfo.NewPath == null) return Array.Empty<EditorCurveBinding>();
                if (binding.path == gameObjectInfo.NewPath) return null;
                binding.path = gameObjectInfo.NewPath;
                return new[] { binding };
            }
        }
    }
}
