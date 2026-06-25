<?xml version="1.0" encoding="UTF-8"?>
<xsl:stylesheet version="3.0" xmlns:xsl="http://www.w3.org/1999/XSL/Transform" xmlns:xs="http://www.w3.org/2001/XMLSchema" xmlns:fn="http://www.w3.org/2005/xpath-functions" xmlns:ct="urn:BimHouse:CommonDataType" xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xmlns:bf='urn:BimHouse:XslFunctions'>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/NewElementResolverStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/PersonStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/OrgStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/ConstructionObjectStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/DocumentStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/MeasuringDeviceStyles.xsl"/>
	<xsl:include href="file:///E:/ArchAndDesign/BIMObjects/Templates/Sheet%20Templates/CustomXmlParts/AddressStyles.xsl"/>
	<xsl:output method="xml" version="1.0" encoding="UTF-8" indent="yes"/>

	<xsl:template match="//processing-instruction()"/>

	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.ФайлБазовыхДанных')]" mode="presenting"/>
	
	<xsl:template match="*[bf:InstanceOf(.,'urn:BimHouse:CommonDataType','Тип.Базовый.УсловияПроведенияИзмерений.ФазаНоль')]" mode="presenting">
		<xsl:element name="{name()}" namespace="{fn:namespace-uri()}">
			<xsl:copy-of select="@*"/>
			<xsl:apply-templates select="./node()" mode="#current"/>
						<xsl:element name="ct:Представления" namespace="urn:BimHouse:CommonDataType">
				<xsl:element name="ct:ТипПитающейСети" namespace="urn:BimHouse:CommonDataType">
					<xsl:for-each select="ct:ПитающаяСеть/ct:Сеть">
						<xsl:if test="position() > 1">
							<xsl:text>, </xsl:text>
						</xsl:if>
						<xsl:value-of select="ct:КоличествоФаз"/>
						<xsl:text>-фазная</xsl:text>
						<xsl:if test="ct:НапряжениеФазаНоль or ct:НапряжениеФазаФаза">
							<xsl:text> (</xsl:text>
								<xsl:if test="ct:НапряжениеФазаНоль">
									<xsl:text>Uфн=</xsl:text>
									<xsl:value-of select="ct:НапряжениеФазаНоль"/>
									<xsl:text>В</xsl:text>
								</xsl:if>
								<xsl:if test="ct:НапряжениеФазаНоль and ct:НапряжениеФазаФаза">
									<xsl:text>, </xsl:text>
								</xsl:if>
								<xsl:if test="ct:НапряжениеФазаФаза">
									<xsl:text>Uфф=</xsl:text>
									<xsl:value-of select="ct:НапряжениеФазаФаза"/>
									<xsl:text>В</xsl:text>
								</xsl:if>
							<xsl:text>)</xsl:text>
						</xsl:if>
					</xsl:for-each>
				</xsl:element>
			</xsl:element>
		</xsl:element>
	</xsl:template>

</xsl:stylesheet>