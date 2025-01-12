﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace SpaceTracker
{
    public class SpaceExtractor
    {

        //private Neo4JConnector Neo4jConnector;
        //private SQLiteConnector SqLiteConnector;
        private readonly CommandManager cmdManager;


        /// <summary>
        /// Dflt constructor
        /// </summary>
        public SpaceExtractor()
        {
            var Neo4jConnector = new Neo4JConnector();
            var SqLiteConnector = new SQLiteConnector();
            cmdManager = new CommandManager(Neo4jConnector, SqLiteConnector);
        }

        /// <summary>
        /// Extracts the existing situation from a model 
        /// </summary>
        /// <param name="doc"></param>
        public void CreateInitialGraph(Document doc)
        {
            // create stopwatch to measure the elapsed time
            Stopwatch timer = new Stopwatch();
            timer.Start();
            Debug.WriteLine("#--------#\nTimer started.\n#--------#");
            
            // Get all levels
            FilteredElementCollector collector = new FilteredElementCollector(doc);
            IList<Element> levels = collector.OfClass(typeof(Level)).ToElements();

            // Iterate over all levels
            foreach (var lvl in levels)
            {
                Debug.WriteLine($"Level: {lvl.Name}, ID: {lvl.Id}");

                string cy = "MERGE (l:Level{Name: \"" + lvl.Name + "\", ElementId: " + lvl.Id + "})";
                cmdManager.cypherCommands.Add(cy);

                string sql = "INSERT INTO Level (ElementId, Name) VALUES (" + lvl.Id + ", '" + lvl.Name + "');";
                cmdManager.sqlCommands.Add(sql);

                // get all Elements of type Room in the current level
                ElementLevelFilter lvlFilter = new ElementLevelFilter(lvl.Id);
                collector = new FilteredElementCollector(doc);
                IList<Element> rooms = collector.WherePasses(new RoomFilter()).WherePasses(lvlFilter).ToElements();

                // Iterate over all rooms in that level
                foreach (var element in rooms)
                {
                    var room = (Room)element;

                    // capture result
                    Debug.WriteLine($"Room: {room.Name}, ID: {room.Id}");

                    cy = "MATCH (l:Level{ElementId:" + room.LevelId + "}) " +
                         "MERGE (r:Room{Name: \"" + room.Name + "\", ElementId: " + room.Id + "}) " +
                         "MERGE (l)-[:CONTAINS]->(r)";
                    cmdManager.cypherCommands.Add(cy);

                    sql = "INSERT INTO Room (ElementId, Name) VALUES (" + room.Id + ", '" + room.Name + "');";
                    cmdManager.sqlCommands.Add(sql);
                    //make level connection
                    sql = "INSERT INTO contains (LevelId, ElementId) VALUES (" + room.LevelId + ", '" + room.Id + "');";
                    cmdManager.sqlCommands.Add(sql);

                    // get all boundaries
                    IList<IList<BoundarySegment>> boundaries
                    = room.GetBoundarySegments(new SpatialElementBoundaryOptions());


                    foreach (IList<BoundarySegment> b in boundaries)
                    {
                        // Iterate over all elements adjacent to current room
                        foreach (BoundarySegment s in b)
                        {

                            // get neighbor element
                            ElementId neighborId = s.ElementId;
                            if (neighborId.IntegerValue == -1)
                            {
                                Debug.WriteLine("Something went wrong when extracting Element ID " + neighborId);
                                continue;
                            }

                            Element neighbor = doc.GetElement(neighborId);

                            if (neighbor is Wall)
                            {
                                Debug.WriteLine($"\tNeighbor Type: Wall - ID: {neighbor.Id}");

                                cy = "MATCH (r:Room{ElementId:" + room.Id + "}) " +
                                     "MATCH (l:Level{ElementId:" + neighbor.LevelId + "}) " +
                                     "MERGE (w:Wall{ElementId: " + neighbor.Id + ", Name: \"" + neighbor.Name + "\"})  " +
                                     "MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                                cmdManager.cypherCommands.Add(cy);

                                // create the sql queries, and then check if they have already been executed
                                // this is sometimes necessary because walls can be adjacent to multiple rooms
                                sql = "INSERT INTO Wall (ElementId, Name) VALUES (" + neighbor.Id + ", '" + neighbor.Name + "');";
                                if (!cmdManager.sqlCommands.Contains(sql))
                                {
                                    cmdManager.sqlCommands.Add(sql);
                                }                                
                                sql = "INSERT INTO bounds (WallId, RoomId) VALUES (" + neighbor.Id + ", " + room.Id + ");";
                                if (!cmdManager.sqlCommands.Contains(sql))
                                {
                                    cmdManager.sqlCommands.Add(sql);
                                }
                                // make level connection
                                sql = "INSERT INTO contains (LevelId, ElementId) VALUES (" + neighbor.LevelId + ", " + neighbor.Id + ");";
                                if (!cmdManager.sqlCommands.Contains(sql))
                                {
                                    cmdManager.sqlCommands.Add(sql);
                                }
                            }

                            else
                            {
                                Debug.WriteLine("\tNeighbor Type: Undefined - ID: " + neighbor.Id);
                            }
                        }
                    }
                }
                Debug.WriteLine("--");

                // get all doors at current level
                var doorCollector = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Doors).OfClass(typeof(FamilyInstance)).WherePasses(lvlFilter);

                var doors = doorCollector.ToElements();

                // Iterate over all doors at current level
                foreach (var door in doors)
                {
                    var inst = (FamilyInstance)door;
                    var wall = inst.Host;
                    Debug.WriteLine($"Door ID: {door.Id}, HostId: {wall.Id}");

                    cy = "MATCH (w:Wall{ElementId:" + wall.Id + "})" +
                         "MATCH (l:Level{ElementId:" + door.LevelId + "})" +
                         "MERGE (d:Door{ElementId:" + inst.Id.IntegerValue + ", Name: \"" + inst.Name + "\" })" +
                         "MERGE (l)-[:CONTAINS]->(d)-[:CONTAINED_IN]->(w)";
                    cmdManager.cypherCommands.Add(cy);


                    sql = "INSERT INTO Door (ElementId, Name, WallId) VALUES (" + door.Id + ", '" + door.Name + "', " + wall.Id + ");";
                    cmdManager.sqlCommands.Add(sql);
                    // insert level into table
                    sql = "INSERT INTO contains (LevelId, ElementId) VALUES (" + door.LevelId + ", " + door.Id + ");";
                    cmdManager.sqlCommands.Add(sql);
                }
            }

            // write commands to file
            var cyCmds = string.Join("\n", cmdManager.cypherCommands);
            File.WriteAllText(@"C:\sqlite_tmp\neo4jcmds.txt", cyCmds);

            var sqlCmds = string.Join("\n", cmdManager.sqlCommands);
            File.WriteAllText(@"C:\sqlite_tmp\neo4jcmds.txt", sqlCmds);

            // print out the elapsed time and stop the timer
            Debug.WriteLine($"#--------#\nTimer stopped: {timer.ElapsedMilliseconds}ms\n#--------#");
            timer.Stop();
        }

        // Deletes all previously existing data (convenient for debugging)
        public void DeleteExistingGraph()
        {
            Debug.WriteLine("Existing graph is being deleted...");
            // Delete all neo4j data
            string cy = "MATCH (n) DETACH DELETE n";
            cmdManager.cypherCommands.Add(cy);

            Debug.WriteLine("Existing table data is being deleted...\n");
            // Delete all sqlite data
            string sql = "DELETE FROM Level";
            cmdManager.sqlCommands.Add(sql);
            sql = "DELETE FROM Room";
            cmdManager.sqlCommands.Add(sql);
            sql = "DELETE FROM Wall";
            cmdManager.sqlCommands.Add(sql);
            sql = "DELETE FROM Door";
            cmdManager.sqlCommands.Add(sql);
            sql = "DELETE FROM contains";
            cmdManager.sqlCommands.Add(sql);
            sql = "DELETE FROM bounds";
            cmdManager.sqlCommands.Add(sql);

        }

        public void UpdateGraph(Document doc, ICollection<ElementId> addedElementIds, ICollection<ElementId> deletedElementIds, ICollection<ElementId> modifiedElementIds)
        {
            Debug.WriteLine("Starting to update Graph...\n");
            string cy;
            string sql;
            // delete nodes
            foreach (ElementId id in deletedElementIds)
            {
                Debug.WriteLine($"Deleting Node with ID: {id}");
                Element e = doc.GetElement(id);

                cy = "MATCH (e {ElementId: " + id + "}) DETACH DELETE e";
                cmdManager.cypherCommands.Add(cy);

                if (typeof(Room).IsAssignableFrom(e.GetType()))
                {
                    sql = "DELETE FROM Room WHERE ElementId = " + id;
                    cmdManager.sqlCommands.Add(sql);

                    sql = "DELETE FROM bounds WHERE RoomId = " + id;
                    cmdManager.sqlCommands.Add(sql);

                    sql = "DELETE FROM contains WHERE ElementId = " + id;
                    cmdManager.sqlCommands.Add(sql);
                }
                else if (typeof(Wall).IsAssignableFrom(e.GetType()))
                {
                    sql = "DELETE FROM Wall WHERE ElementId = " + id;
                    cmdManager.sqlCommands.Add(sql);

                    sql = "DELETE FROM bounds WHERE WallId = " + id;
                    cmdManager.sqlCommands.Add(sql);

                    sql = "DELETE FROM contains WHERE ElementId = " + id;
                    cmdManager.sqlCommands.Add(sql);
                }
                else if (typeof(Level).IsAssignableFrom(e.GetType()))
                {
                    sql = "DELETE FROM Level Where ElementId = " + id;
                    cmdManager.sqlCommands.Add(sql);

                    sql = "DELTE FROM contains WHERE LevelId = " + id;
                    cmdManager.sqlCommands.Add(sql);
                }
                


            }

            // modify nodes
            foreach (ElementId id in modifiedElementIds)
            {
                Element e = doc.GetElement(id);


                // change properties
                cy = "MATCH (e {ElementId: " + id + "}) SET e.Name = '" + e.Name + "'";
                cmdManager.cypherCommands.Add(cy);

                // change relationships
                if (typeof(Room).IsAssignableFrom(e.GetType()))
                {
                    Debug.WriteLine($"Modifying Node with ID: {id} and Name: {e.Name}");

                    sql = "UPDATE Room SET Name = " + e.Name + "WHERE ElementId = " + id;
                    cmdManager.sqlCommands.Add(sql);

                    Room room = e as Room;
                    // get all boundaries
                    IList<IList<BoundarySegment>> boundaries
                    = room.GetBoundarySegments(new SpatialElementBoundaryOptions());

                    foreach (IList<BoundarySegment> b in boundaries)
                    {
                        // Iterate over all elements adjacent to current room
                        foreach (BoundarySegment s in b)
                        {
                            // get neighbor element
                            ElementId neighborId = s.ElementId;
                            if (neighborId.IntegerValue == -1)
                            {
                                Debug.WriteLine("Something went wrong when extracting Element ID " + neighborId);
                                continue;
                            }

                            Element neighbor = doc.GetElement(neighborId);
                            var levelId = neighbor.LevelId;

                            if (neighbor is Wall)
                            {
                                cy = "MATCH (r:Room{ElementId: " + room.Id + "})" +
                                     "MATCH (w:Wall{ElementId: " + neighbor.Id + "})" +
                                     "MATCH (l:Level{ElementId: " + levelId + "})" +
                                     "MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                                cmdManager.cypherCommands.Add(cy);

                                sql = "INSERT INTO Wall (ElementId, Name) VALUES (" + neighbor.Id + ", '" + neighbor.Name + "');";
                                cmdManager.sqlCommands.Add(sql);

                                sql = "INSERT INTO bounds (WallId, RoomId) VALUES (" + neighbor.Id + ", " + room.Id + ");";
                                cmdManager.sqlCommands.Add(sql);

                                sql = "INSERT INTO contains (LevelId, ElementId) VALUES (" + neighbor.LevelId + ", " + neighbor.Id + ");";
                                cmdManager.sqlCommands.Add(sql);

                                Debug.WriteLine($"Modified Room with ID: {id} and Name: {e.Name}");


                            }
                        }
                    }
                }
                if (typeof(Wall).IsAssignableFrom(e.GetType()))
                {
                    Debug.WriteLine($"Modifying Node with ID: {id} and Name: {e.Name}");

                    sql = "UPDATE Wall SET Name = " + e.Name + "WHERE ElementId = " + id;
                    cmdManager.sqlCommands.Add(sql);

                    // get the room
                    IList<Element> rooms = getRoomFromWall(doc, e as Wall);


                    foreach (Element element in rooms)
                    {
                        var room = (Room)element;
                        var levelId = room.LevelId;
                        cy = "MATCH (w:Wall{ElementId: " + id + "}) " +
                             "MATCH (r:Room{ElementId: " + room.Id + "})" +
                             "MATCH (l:Level{ELementId: " + levelId + "})" +
                             "MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                        cmdManager.cypherCommands.Add(cy);

                        sql = "INSERT INTO Wall (ElementId, Name) VALUES (" + id + ", '" + e.Name + "');";
                        cmdManager.sqlCommands.Add(sql);
                        Debug.WriteLine($"Modified Wall with ID: {id} and Name: {e.Name} ");
                    }
                }

                if (typeof(Level).IsAssignableFrom(e.GetType()))
                {
                    Debug.WriteLine($"Modifying Node with ID: {id} and Name: {e.Name}");

                    sql = "UPDATE Level SET Name = " + e.Name + "WHERE ElementId = " + id;
                    cmdManager.sqlCommands.Add(sql);

                    ElementLevelFilter lvlFilter = new ElementLevelFilter(id);
                    FilteredElementCollector collector = new FilteredElementCollector(doc);
                    IList<Element> elementsOnLevel = collector.WherePasses(lvlFilter).ToElements();

                    foreach (Element element in elementsOnLevel)
                    {
                        if (typeof(Wall).IsAssignableFrom(element.GetType()))
                        {
                            cy = "MATCH (l:Level{ElementId: " + id + "}) " +
                                 "MATCH (w:Wall{ElementId: " + element.Id + "}) " +
                                 "MERGE (l)-[:CONTAINS]->(w)";
                            cmdManager.cypherCommands.Add(cy);

                            sql = "INSERT INTO Wall (ElementId, Name) VALUES (" + id + ", '" + e.Name + "');";
                            cmdManager.sqlCommands.Add(sql);

                            sql = "INSERT INTO contains (LevelId, ElementId) VALUES (" + id + ", " + element.Id + ");";
                            cmdManager.sqlCommands.Add(sql);
                        }
                        else if (typeof(Room).IsAssignableFrom(element.GetType()))
                        {
                            cy = "MATCH (l:Level{ElementId: " + id + "}) " +
                                 "MATCH (r:Room{ElementId: " + element.Id + "}) " +
                                 "MERGE (l)-[:CONTAINS]->(r)";
                            cmdManager.cypherCommands.Add(cy);

                            sql = "INSERT INTO Wall (ElementId, Name) VALUES (" + id + ", '" + e.Name + "');";
                            cmdManager.sqlCommands.Add(sql);

                            sql = "INSERT INTO contains (LevelId, ElementId) VALUES (" + id + ", '" + element.Id + "');";
                            cmdManager.sqlCommands.Add(sql);
                        }

                        Debug.WriteLine($"Modified Level with ID: {id} and Name: {e.Name}");
                    }
                }
            }

            //add nodes
            foreach (ElementId id in addedElementIds)
            {
                Element e = doc.GetElement(id);

                if (typeof(Room).IsAssignableFrom(e.GetType()))
                {
                    var room = (Room)e;

                    // capture result
                    Debug.WriteLine($"Room: {room.Name}, ID: {room.Id}");

                    cy = "MATCH (l:Level{ElementId:" + room.LevelId + "}) " +
                         "MERGE (r:Room{Name: \"" + room.Name + "\", ElementId: " + room.Id + "}) " +
                         "MERGE (l)-[:CONTAINS]->(r)";
                    cmdManager.cypherCommands.Add(cy);

                    // get all boundaries
                    IList<IList<BoundarySegment>> boundaries
                    = room.GetBoundarySegments(new SpatialElementBoundaryOptions());


                    foreach (IList<BoundarySegment> b in boundaries)
                    {
                        // Iterate over all elements adjacent to current room
                        foreach (BoundarySegment s in b)
                        {

                            // get neighbor element
                            ElementId neighborId = s.ElementId;
                            if (neighborId.IntegerValue == -1)
                            {
                                Debug.WriteLine("Something went wrong when extracting Element ID " + neighborId);
                                continue;
                            }

                            Element neighbor = doc.GetElement(neighborId);

                            if (neighbor is Wall)
                            {
                                Debug.WriteLine($"\tNeighbor Type: Wall - ID: {neighbor.Id}");

                                cy = "MATCH (r:Room{ElementId:" + room.Id + "}) " +
                                     "MATCH (l:Level{ElementId:" + neighbor.LevelId + "}) " +
                                     "MERGE (w:Wall{ElementId: " + neighbor.Id + ", Name: \"" + neighbor.Name + "\"}) " +
                                     "MERGE (l)-[:CONTAINS]->(w)-[:BOUNDS]->(r)";
                                cmdManager.cypherCommands.Add(cy);
                            }
                            else
                            {
                                Debug.WriteLine("\tNeighbor Type: Undefined - ID: " + neighbor.Id);
                            }
                        }
                    }
                }
                if (typeof(Wall).IsAssignableFrom(e.GetType()))
                {
                    var wall = (Wall)e;
                    Debug.WriteLine($"Room: {wall.Name}, ID: {wall.Id}");

                    cy = "MERGE (w:Wall{ElementId: " + wall.Id + "})";
                    cmdManager.cypherCommands.Add(cy);
                }


            }
        }

        public IList<Element> getRoomFromWall(Document doc, Wall wall)
        {
            BoundingBoxXYZ wall_bb = wall.get_BoundingBox(null);
            Outline outl = new Outline(wall_bb.Min, wall_bb.Max);
            ElementFilter bbfilter = new BoundingBoxIntersectsFilter(outl);

            IList<Element> rooms = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WherePasses(bbfilter).ToElements();

            return rooms;

        }
    }
}