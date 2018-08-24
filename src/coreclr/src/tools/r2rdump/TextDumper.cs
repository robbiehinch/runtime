﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Xml;

namespace R2RDump
{
    class TextDumper : Dumper
    {
        public TextDumper(R2RReader r2r, TextWriter writer, bool raw, bool header, bool disasm, Disassembler disassembler, bool unwind, bool gc, bool sectionContents)
        {
            _r2r = r2r;
            _writer = writer;

            _raw = raw;
            _header = header;
            _disasm = disasm;
            _disassembler = disassembler;
            _unwind = unwind;
            _gc = gc;
            _sectionContents = sectionContents;
        }

        internal override void Begin()
        {
            _writer.WriteLine($"Filename: {_r2r.Filename}");
            _writer.WriteLine($"Machine: {_r2r.Machine}");
            _writer.WriteLine($"ImageBase: 0x{_r2r.ImageBase:X8}");
            SkipLine();
        }

        internal override void End()
        {
            _writer.WriteLine("=============================================================");
            SkipLine();
        }

        internal override void WriteDivider(string title)
        {
            int len = 61 - title.Length - 2;
            _writer.WriteLine(new String('=', len / 2) + " " + title + " " + new String('=', (int)Math.Ceiling(len / 2.0)));
            SkipLine();
        }

        internal override void WriteSubDivider()
        {
            _writer.WriteLine("_______________________________________________");
            SkipLine();
        }

        internal override void SkipLine()
        {
            _writer.WriteLine();
        }

        /// <summary>
        /// Dumps the R2RHeader and all the sections in the header
        /// </summary>
        internal override void DumpHeader(bool dumpSections)
        {
            _writer.WriteLine(_r2r.R2RHeader.ToString());

            if (_raw)
            {
                DumpBytes(_r2r.R2RHeader.RelativeVirtualAddress, (uint)_r2r.R2RHeader.Size);
            }
            SkipLine();
            if (dumpSections)
            {
                WriteDivider("R2R Sections");
                _writer.WriteLine($"{_r2r.R2RHeader.Sections.Count} sections");
                SkipLine();
                
                foreach (R2RSection section in _r2r.R2RHeader.Sections.Values)
                {
                    DumpSection(section);
                }
            }
            SkipLine();
        }

        /// <summary>
        /// Dumps one R2RSection
        /// </summary>
        internal override void DumpSection(R2RSection section, XmlNode parentNode = null)
        {
            WriteSubDivider();
            _writer.WriteLine(section.ToString());

            if (_raw)
            {
                DumpBytes(section.RelativeVirtualAddress, (uint)section.Size);
                SkipLine();
            }
            if (_sectionContents)
            {
                DumpSectionContents(section);
                SkipLine();
            }
        }

        internal override void DumpAllMethods()
        {
            WriteDivider("R2R Methods");
            _writer.WriteLine($"{_r2r.R2RMethods.Count} methods");
            SkipLine();
            foreach (R2RMethod method in _r2r.R2RMethods)
            {
                DumpMethod(method);
            }
        }

        /// <summary>
        /// Dumps one R2RMethod. 
        /// </summary>
        internal override void DumpMethod(R2RMethod method, XmlNode parentNode = null)
        {
            WriteSubDivider();
            _writer.WriteLine(method.ToString());

            if (_gc && method.GcInfo != null)
            {
                _writer.WriteLine("GcInfo:");
                _writer.Write(method.GcInfo);

                if (_raw)
                {
                    DumpBytes(method.GcInfo.Offset, (uint)method.GcInfo.Size, null, "", false);
                }
            }
            SkipLine();

            foreach (RuntimeFunction runtimeFunction in method.RuntimeFunctions)
            {
                DumpRuntimeFunction(runtimeFunction);
            }
        }

        /// <summary>
        /// Dumps one runtime function. 
        /// </summary>
        internal override void DumpRuntimeFunction(RuntimeFunction rtf, XmlNode parentNode = null)
        {
            _writer.WriteLine(rtf.Method.SignatureString);
            _writer.Write($"{rtf}");

            if (_disasm)
            {
                DumpDisasm(rtf, _r2r.GetOffset(rtf.StartAddress));
            }

            if (_raw)
            {
                _writer.WriteLine("Raw Bytes:");
                DumpBytes(rtf.StartAddress, (uint)rtf.Size);
            }
            if (_unwind)
            {
                _writer.WriteLine("UnwindInfo:");
                _writer.Write(rtf.UnwindInfo);
                if (_raw)
                {
                    DumpBytes(rtf.UnwindRVA, (uint)rtf.UnwindInfo.Size);
                }
            }
            SkipLine();
        }

        /// <summary>
        /// Dumps disassembly and register liveness
        /// </summary>
        internal override void DumpDisasm(RuntimeFunction rtf, int imageOffset, XmlNode parentNode = null)
        {
            int rtfOffset = 0;
            int codeOffset = rtf.CodeOffset;
            while (rtfOffset < rtf.Size)
            {
                string instr;
                int instrSize = _disassembler.GetInstruction(rtf, imageOffset, rtfOffset, out instr);

                if (_r2r.Machine == Machine.Amd64 && ((Amd64.UnwindInfo)rtf.UnwindInfo).UnwindCodes.ContainsKey(codeOffset))
                {
                    List<Amd64.UnwindCode> codes = ((Amd64.UnwindInfo)rtf.UnwindInfo).UnwindCodes[codeOffset];
                    foreach (Amd64.UnwindCode code in codes)
                    {
                        _writer.Write($"\t\t\t\t{code.UnwindOp} {code.OpInfoStr}");
                        if (code.NextFrameOffset != -1)
                        {
                            _writer.WriteLine($" - {code.NextFrameOffset}");
                        }
                        _writer.WriteLine();
                    }
                }

                if (rtf.Method.GcInfo != null && rtf.Method.GcInfo.Transitions.ContainsKey(codeOffset))
                {
                    foreach (BaseGcTransition transition in rtf.Method.GcInfo.Transitions[codeOffset])
                    {
                        _writer.WriteLine($"\t\t\t\t{transition.ToString()}");
                    }
                }

                /* According to https://msdn.microsoft.com/en-us/library/ck9asaa9.aspx and src/vm/gcinfodecoder.cpp
                 * UnwindCode and GcTransition CodeOffsets are encoded with a -1 adjustment (that is, it's the offset of the start of the next instruction)
                 */
                _writer.Write(instr);

                CoreDisTools.ClearOutputBuffer();
                rtfOffset += instrSize;
                codeOffset += instrSize;
            }
        }

        /// <summary>
        /// Prints a formatted string containing a block of bytes from the relative virtual address and size
        /// </summary>
        internal override void DumpBytes(int rva, uint size, XmlNode parentNode = null, string name = "Raw", bool convertToOffset = true)
        {
            int start = rva;
            if (convertToOffset)
                start = _r2r.GetOffset(rva);
            if (start > _r2r.Image.Length || start + size > _r2r.Image.Length)
            {
                throw new IndexOutOfRangeException();
            }

            _writer.Write("    ");
            if (rva % 16 != 0)
            {
                int floor = rva / 16 * 16;
                _writer.Write($"{floor:X8}:");
                _writer.Write(new String(' ', (rva - floor) * 3));
            }
            for (uint i = 0; i < size; i++)
            {
                if ((rva + i) % 16 == 0)
                {
                    _writer.Write($"{rva + i:X8}:");
                }
                _writer.Write($" {_r2r.Image[start + i]:X2}");
                if ((rva + i) % 16 == 15 && i != size - 1)
                {
                    SkipLine();
                    _writer.Write("    ");
                }
            }
            SkipLine();
        }

        internal override void DumpSectionContents(R2RSection section, XmlNode parentNode = null)
        {
            switch (section.Type)
            {
                case R2RSection.SectionType.READYTORUN_SECTION_AVAILABLE_TYPES:
                    uint availableTypesSectionOffset = (uint)_r2r.GetOffset(section.RelativeVirtualAddress);
                    NativeParser availableTypesParser = new NativeParser(_r2r.Image, availableTypesSectionOffset);
                    NativeHashtable availableTypes = new NativeHashtable(_r2r.Image, availableTypesParser, (uint)(availableTypesSectionOffset + section.Size));
                    _writer.WriteLine(availableTypes.ToString());

                    foreach (string name in _r2r.AvailableTypes)
                    {
                        _writer.WriteLine(name);
                    }
                    break;
                case R2RSection.SectionType.READYTORUN_SECTION_METHODDEF_ENTRYPOINTS:
                    NativeArray methodEntryPoints = new NativeArray(_r2r.Image, (uint)_r2r.GetOffset(section.RelativeVirtualAddress));
                    _writer.Write(methodEntryPoints.ToString());
                    break;
                case R2RSection.SectionType.READYTORUN_SECTION_INSTANCE_METHOD_ENTRYPOINTS:
                    uint instanceSectionOffset = (uint)_r2r.GetOffset(section.RelativeVirtualAddress);
                    NativeParser instanceParser = new NativeParser(_r2r.Image, instanceSectionOffset);
                    NativeHashtable instMethodEntryPoints = new NativeHashtable(_r2r.Image, instanceParser, (uint)(instanceSectionOffset + section.Size));
                    _writer.Write(instMethodEntryPoints.ToString());
                    break;
                case R2RSection.SectionType.READYTORUN_SECTION_RUNTIME_FUNCTIONS:
                    int rtfOffset = _r2r.GetOffset(section.RelativeVirtualAddress);
                    int rtfEndOffset = rtfOffset + section.Size;
                    int rtfIndex = 0;
                    while (rtfOffset < rtfEndOffset)
                    {
                        int startRva = NativeReader.ReadInt32(_r2r.Image, ref rtfOffset);
                        int endRva = -1;
                        if (_r2r.Machine == Machine.Amd64)
                        {
                            endRva = NativeReader.ReadInt32(_r2r.Image, ref rtfOffset);
                        }
                        int unwindRva = NativeReader.ReadInt32(_r2r.Image, ref rtfOffset);
                        _writer.WriteLine($"Index: {rtfIndex}");
                        _writer.WriteLine($"\tStartRva: 0x{startRva:X8}");
                        if (endRva != -1)
                            _writer.WriteLine($"\tEndRva: 0x{endRva:X8}");
                        _writer.WriteLine($"\tUnwindRva: 0x{unwindRva:X8}");
                        rtfIndex++;
                    }
                    break;
                case R2RSection.SectionType.READYTORUN_SECTION_COMPILER_IDENTIFIER:
                    _writer.WriteLine(_r2r.CompilerIdentifier);
                    break;
                case R2RSection.SectionType.READYTORUN_SECTION_IMPORT_SECTIONS:
                    foreach (R2RImportSection importSection in _r2r.ImportSections)
                    {
                        _writer.Write(importSection.ToString());
                        if (_raw && importSection.Entries.Count != 0)
                        {
                            if (importSection.SectionRVA != 0)
                            {
                                _writer.WriteLine("Section Bytes:");
                                DumpBytes(importSection.SectionRVA, (uint)importSection.SectionSize);
                            }
                            if (importSection.SignatureRVA != 0)
                            {
                                _writer.WriteLine("Signature Bytes:");
                                DumpBytes(importSection.SignatureRVA, (uint)importSection.Entries.Count * sizeof(int));
                            }
                            if (importSection.AuxiliaryDataRVA != 0 && importSection.AuxiliaryData != null)
                            {
                                _writer.WriteLine("AuxiliaryData Bytes:");
                                DumpBytes(importSection.AuxiliaryDataRVA, (uint)importSection.AuxiliaryData.Size);
                            }
                        }
                        foreach (R2RImportSection.ImportSectionEntry entry in importSection.Entries)
                        {
                            _writer.WriteLine(entry.ToString());
                        }
                        _writer.WriteLine();
                    }
                    break;
            }
        }

        internal override XmlNode DumpQueryCount(string q, string title, int count)
        {
            _writer.WriteLine(count + " result(s) for \"" + q + "\"");
            SkipLine();
            return null;
        }
    }
}
