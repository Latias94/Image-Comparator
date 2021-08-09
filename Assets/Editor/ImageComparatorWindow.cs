//
// Image Comparator. Copyright (c) 2021-2021 Latias94 (www.frankorz.com). See LICENSE.md
// https://github.com/Latias94/UnityImageComparator/
//

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

public class ImageComparatorWindow : EditorWindow
{
    /// <summary>
    /// Compute Shader 返回结果的结构 
    /// </summary>
    struct CompareResult
    {
        public uint different;
    }

    /// <summary>
    /// 保存比较的两张图片的索引
    /// 索引从 _texturePaths, _textureGUIDs 获取路径和对应 GUID
    /// </summary>
    struct PreProcessUnit
    {
        public readonly int sourceIndex;
        public readonly int compareIndex;
        public Vector2Int textureSize; // width height

        public PreProcessUnit(int sourceIndex, int compareIndex)
        {
            this.sourceIndex = sourceIndex;
            this.compareIndex = compareIndex;
            textureSize = Vector2Int.zero;
        }
    }

    private ComputeShader _shader;
    private int _kernelHandle;
    
    private ComputeBuffer _buffer;
    /// <summary>
    /// 存放 Compute Shader 返回结果的数组，Compute Buffer 是其与 Compute Shader 沟通的桥梁
    /// </summary>
    private CompareResult[] _results;

    /// <summary>
    /// 美术资源的路径，用 index 来查找
    /// </summary>
    private List<string> _texturePaths;
    /// <summary>
    /// 美术资源的 GUID，用 index 来查找
    /// </summary>
    private string[] _textureGUIDs;

    /// <summary>
    /// 预处理要执行的任务列表
    /// </summary>
    private List<PreProcessUnit> _allTasks;
    /// <summary>
    /// 处理后发现相同图片的任务列表
    /// </summary>
    private List<PreProcessUnit> _sameImagesInResult;

    private Stopwatch _stopWatch = new Stopwatch();

    private long _totalSize;

    private const int MAX_TEXTURE_WIDTH = 2048;
    private const int MAX_TEXTURE_HEIGHT = 2048;

    /// <summary>
    /// 真正走 Compute Shader 计算的次数（不同高度宽度的图片相比较会直接跳过）
    /// </summary>
    private uint _realCompareCount;

    private EditorCoroutine _coroutine;

    /// <summary>
    /// 分析的文件夹路径
    /// </summary>
    public string[] listOfFolders;

    /// <summary>
    /// 相似度
    /// </summary>
    private float _acceptDifferencePercentage;

    [MenuItem("MyTools/找相同的图片资源")]
    private static void OpenWindow()
    {
        var window = GetWindow<ImageComparatorWindow>();
        window.Show();
    }

    private void Awake()
    {
        InitComparator();
    }

    private void OnDisable()
    {
        if (_coroutine != null)
        {
            this.StopCoroutine(_coroutine);
        }

        if (_buffer != null)
        {
            _buffer.Dispose();
        }
    }

    private void OnGUI()
    {
        if (GUILayout.Button("执行比较-完全相同"))
        {
            _acceptDifferencePercentage = 0f;
            Execute();
        }

        if (GUILayout.Button("执行比较-90%相似"))
        {
            _acceptDifferencePercentage = 0.1f;
            Execute();
        }

        if (GUILayout.Button("执行比较-所有结果"))
        {
            _acceptDifferencePercentage = 1f;
            Execute();
        }
    }

    private void InitComparator()
    {
        // 硬编码, 有兴趣的可以加 GUI
        listOfFolders = new[] { "Assets/SpritesForTesting" };

        FindComputeShader();
        InitComputeShader();
    }


    private void Execute()
    {
        _stopWatch.Restart();
        if (_shader == null)
        {
            // reload window if needed.
            InitComparator();
        }

        if (listOfFolders == null)
        {
            Debug.LogError("请拖入文件夹！");
            return;
        }

        var paths = listOfFolders.Where(x => !string.IsNullOrEmpty(x)).ToArray();
        if (paths.Length == 0)
        {
            Debug.LogError("请拖入文件夹！");
            return;
        }

        _textureGUIDs = AssetDatabase.FindAssets("t:texture2D", paths);

        _coroutine = this.StartCoroutine(StartProgram(_textureGUIDs));
    }


    private IEnumerator StartProgram(string[] textureGUIDs)
    {
        int finishProcess = 0;
        _realCompareCount = 0;
        _totalSize = 0;

        var batch = CreateTasks(textureGUIDs, 128);
        _sameImagesInResult = new List<PreProcessUnit>();

        foreach (var units in batch)
        {
            FindDifferences(units, (data, differences) =>
            {
                finishProcess++;
                int totalPixels = data.textureSize.x * data.textureSize.y;
                float differencePercentage = (float)differences / totalPixels;
                if (differencePercentage <= _acceptDifferencePercentage)
                {
                    _sameImagesInResult.Add(data);
                    // Debug.Log("不同像素数:" + differences);
                    LogUnit(data, differencePercentage, differences);
                }

                EditorUtility.DisplayProgressBar("处理进度", $"{finishProcess} / {_allTasks.Count}",
                    finishProcess / (float)_allTasks.Count);
            });
            yield return null;
        }

        _stopWatch.Stop();
        TimeSpan ts = _stopWatch.Elapsed;
        string elapsedTime = $"{ts.Hours:00}小时{ts.Minutes:00}分{ts.Seconds:00}秒{ts.Milliseconds / 10:00}毫秒";

        EditorUtility.ClearProgressBar();

        Debug.Log(
            $"处理完毕，共耗时：{elapsedTime}，比较：{_realCompareCount}/{_allTasks.Count}次，" +
            $"图片共有{textureGUIDs.Length}组，相同的图片共有{_sameImagesInResult.Count}组。");

        Debug.Log($"重复的大小（相同的两张纹理中一张的大小）为：{BytesToString(_totalSize)}");
    }

    private void LogUnit(PreProcessUnit data, float differentPercentage, uint differences)
    {
        var sourcePath = _texturePaths[data.sourceIndex];
        var comparePath = _texturePaths[data.compareIndex];
        var fileSize = new FileInfo(sourcePath).Length;
        _totalSize += fileSize;
        var fileSizeStr = BytesToString(fileSize);
        Debug.Log($"大小:{fileSizeStr}, 像素差异数：{differences}, 相似度:{1 - differentPercentage:P} {sourcePath}\n {comparePath}");
    }

    private void FindDifferences(List<PreProcessUnit> tasks, Action<PreProcessUnit, uint> callback)
    {
        for (int i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            CheckAndExecute(task, callback);
        }
    }

    private void CreateAllTasks(string[] textureGUIDs)
    {
        _texturePaths = new List<string>(textureGUIDs.Length);
        for (var i = 0; i < textureGUIDs.Length; i++)
        {
            var texturePath = AssetDatabase.GUIDToAssetPath(textureGUIDs[i]);
            _texturePaths.Add(texturePath);
        }

        _allTasks = new List<PreProcessUnit>();
        for (int i = 0; i < textureGUIDs.Length; i++)
        {
            for (int j = i + 1; j < textureGUIDs.Length; j++)
            {
                _allTasks.Add(new PreProcessUnit(i, j));
            }
        }
    }

    private List<List<PreProcessUnit>> CreateTasks(string[] textureGUIDs, uint batchCount = 0)
    {
        CreateAllTasks(textureGUIDs);
        if (batchCount == 0) return new List<List<PreProcessUnit>>(1) { _allTasks };
        var group = Split(_allTasks, batchCount);
        return group;
    }

    public static List<List<T>> Split<T>(List<T> source, uint eachCountInGroup)
    {
        return source
            .Select((x, i) => new { Index = i, Value = x })
            .GroupBy(x => x.Index / eachCountInGroup)
            .Select(x => x.Select(v => v.Value).ToList())
            .ToList();
    }

    private void CheckAndExecute(PreProcessUnit data, Action<PreProcessUnit, uint> callback)
    {
        Texture2D sourceTexture = LoadTextureFromAssetDatabase(data.sourceIndex);
        Texture2D compareTexture = LoadTextureFromAssetDatabase(data.compareIndex);
        if (!CheckIfTextureHaveSameSize(sourceTexture, compareTexture)) return;
        data.textureSize = new Vector2Int(sourceTexture.width, sourceTexture.height);
        _realCompareCount++;

        SetTexturesToShader(sourceTexture, compareTexture);

        uint totalDifference = ProcessComputeShader(data.textureSize);

        callback(data, totalDifference);
    }

    private Texture2D LoadTextureFromAssetDatabase(int index)
    {
        var texturePath = _texturePaths[index];
        var texture2D = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
        return texture2D;
    }

    private static bool CheckIfTextureHaveSameSize(Texture2D sourceTexture, Texture2D compareTexture)
    {
        if (sourceTexture == null || compareTexture == null) return false;
        return sourceTexture.width == compareTexture.width && sourceTexture.height == compareTexture.height;
    }

    public static string BytesToString(long byteCount)
    {
        string[] suf = { "B", "KB", "MB", "GB", "TB", "PB", "EB" }; //Longs run out around EB
        if (byteCount == 0)
            return "0" + suf[0];
        long bytes = Math.Abs(byteCount);
        int place = Convert.ToInt32(Math.Floor(Math.Log(bytes, 1024)));
        double num = Math.Round(bytes / Math.Pow(1024, place), 1);
        return (Math.Sign(byteCount) * num).ToString() + suf[place];
    }


    private void InitComputeShader()
    {
        _kernelHandle = _shader.FindKernel("CSMain");
        uint bufferSize = MAX_TEXTURE_WIDTH * MAX_TEXTURE_HEIGHT;
        if (_buffer == null)
        {
            _buffer = new ComputeBuffer((int)bufferSize, sizeof(uint));
        }

        _results = new CompareResult[bufferSize];
        for (int i = 0; i < bufferSize; i++)
        {
            _results[i] = new CompareResult();
        }

        _buffer.SetData(_results);
        _shader.SetBuffer(_kernelHandle, "resultBuffer", _buffer);
        _shader.SetInt("bufferMaxWidth", MAX_TEXTURE_WIDTH);
    }

    private void FindComputeShader()
    {
        var shaderGuid =
            AssetDatabase.FindAssets("ImageComparator t:computeShader", new[] { "Assets" });
        if (shaderGuid.Length == 0)
        {
            Debug.LogError("Cannot Find Comparator.compute in Assets/");
            return;
        }

        var path = AssetDatabase.GUIDToAssetPath(shaderGuid[0]);
        _shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
    }

    private void SetTexturesToShader(Texture2D sourceTexture, Texture2D compareTexture)
    {
        _shader.SetTexture(_kernelHandle, "sourceTexture", sourceTexture);
        _shader.SetTexture(_kernelHandle, "compareTexture", compareTexture);
    }

    private void DispatchShader(int x, int y)
    {
        _shader.Dispatch(_kernelHandle, x, y, 1);
    }

    private uint ProcessComputeShader(Vector2Int textureSize)
    {
        int x = Mathf.CeilToInt(textureSize.x);
        int y = Mathf.CeilToInt(textureSize.y);
        DispatchShader(x, y);
        _buffer.GetData(_results);
        uint totalDifference = 0;
        for (int i = 0; i < x; i++)
        {
            for (int j = 0; j < y; j++)
            {
                var index = j * MAX_TEXTURE_WIDTH + i;
                var result = _results[index];
                if (result.different == 0) continue;
                totalDifference += result.different;
                // Debug.Log(
                //     $"size: {textureSize.x},{textureSize.y} different: {i},{j} idx:{index}  {result.different}");
            }
        }

        return totalDifference;
    }
}